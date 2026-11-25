using System.Threading.Channels;
using BLL.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Static_X.API.Integrations;
using Xunit;

namespace Static_X.Tests;

public sealed class PasswordRecoveryOutboxWorkerTests
{
    [Theory]
    [InlineData("processed", 1)]
    [InlineData("idle", 10)]
    [InlineData("error", 30)]
    public async Task RecoveryWorker_UsesFreshDisposedScopesAndBoundedDelays(string outcome, int seconds)
    {
        var state = new State { Outcome = outcome };
        var clock = new ControlledClock();
        var logger = new CapturingLogger();
        await using var provider = new ServiceCollection().AddSingleton(state).AddScoped<IPasswordRecoveryDispatcher, Dispatcher>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var worker = new PasswordRecoveryOutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock,
            logger);
        await worker.StartAsync(default);
        var first = await state.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var timer = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.Disposed);
        Assert.True(first.CleanedBeforeDispatch);
        Assert.Equal(TimeSpan.FromSeconds(seconds), timer.Due);
        Assert.Equal(1, state.Count);
        if (outcome == "error")
        {
            var entry = Assert.Single(logger.Entries);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("Private provider detail", entry.Message);
            Assert.Contains("Retrying in 30 seconds", entry.Message);
        }
        else Assert.Empty(logger.Entries);
        timer.Fire();
        var second = await state.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var next = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(first, second);
        Assert.True(second.Disposed);
        Assert.True(second.CleanedBeforeDispatch);
        Assert.Equal(TimeSpan.FromSeconds(seconds), next.Due);
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(next.Disposed);
        Assert.Equal(2, state.Count);
    }

    [Fact]
    public async Task RecoveryWorker_CancellationReachesDispatchAndDisposesScopeWithoutRetry()
    {
        var state = new State { Outcome = "wait" };
        var clock = new ControlledClock();
        await using var provider = new ServiceCollection().AddSingleton(state).AddScoped<IPasswordRecoveryDispatcher, Dispatcher>().BuildServiceProvider();
        using var worker = new PasswordRecoveryOutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<PasswordRecoveryOutboxWorker>.Instance);
        await worker.StartAsync(default);
        var dispatch = await state.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(dispatch.Token.CanBeCanceled);
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(dispatch.Token.IsCancellationRequested);
        Assert.True(dispatch.Disposed);
        Assert.Equal(1, state.Count);
        Assert.False(clock.Timers.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RecoveryWorker_CleanupFailureDisposesScopeAndWaitsThirtySecondsWithoutDispatch()
    {
        var state = new State { Outcome = "cleanup-error" };
        var clock = new ControlledClock();
        await using var provider = new ServiceCollection().AddSingleton(state).AddScoped<IPasswordRecoveryDispatcher, Dispatcher>().BuildServiceProvider();
        using var worker = new PasswordRecoveryOutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<PasswordRecoveryOutboxWorker>.Instance);
        await worker.StartAsync(default);
        var timer = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(30), timer.Due);
        Assert.Equal(0, state.Count);
        Assert.True(state.LastCleanup!.Disposed);
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(timer.Disposed);
    }

    [Fact]
    public async Task RecoveryWorker_CancellationDuringCleanupDisposesScopeWithoutDispatchOrRetry()
    {
        var state = new State { Outcome = "cleanup-wait" };
        var clock = new ControlledClock();
        await using var provider = new ServiceCollection().AddSingleton(state).AddScoped<IPasswordRecoveryDispatcher, Dispatcher>().BuildServiceProvider();
        using var worker = new PasswordRecoveryOutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<PasswordRecoveryOutboxWorker>.Instance);
        await worker.StartAsync(default);
        var cleanup = await state.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleanup.Token.CanBeCanceled);
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleanup.Token.IsCancellationRequested);
        Assert.True(cleanup.Disposed);
        Assert.Equal(0, state.Count);
        Assert.False(clock.Timers.Reader.TryRead(out _));
    }

    private sealed class State
    {
        public string Outcome { get; init; } = "idle";
        public int Count;
        public Dispatcher? LastCleanup;
        public Channel<Dispatcher> Calls { get; } = Channel.CreateUnbounded<Dispatcher>();
    }

    private sealed class CapturingLogger : ILogger<PasswordRecoveryOutboxWorker>
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((formatter(state, exception), exception));
    }

    private sealed class Dispatcher : IPasswordRecoveryDispatcher, IAsyncDisposable
    {
        private readonly State state;
        public bool Disposed { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool CleanedBeforeDispatch { get; private set; }
        public Dispatcher(State state) { this.state = state; }
        public async Task CleanupAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.LastCleanup = this;
            Token = cancellationToken;
            if (state.Outcome == "cleanup-error") throw new IOException("Private cleanup failure.");
            if (state.Outcome == "cleanup-wait")
            {
                state.Calls.Writer.TryWrite(this);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            CleanedBeforeDispatch = true;
        }
        public async Task<bool> DispatchOneAsync(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Interlocked.Increment(ref state.Count);
            state.Calls.Writer.TryWrite(this);
            if (state.Outcome == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (state.Outcome == "error") throw new IOException("Private provider detail must not be logged.");
            return state.Outcome == "processed";
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ControlledClock : TimeProvider
    {
        public Channel<ControlledTimer> Timers { get; } = Channel.CreateUnbounded<ControlledTimer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(callback, state, dueTime);
            Timers.Writer.TryWrite(timer);
            return timer;
        }
    }

    private sealed class ControlledTimer : ITimer
    {
        private readonly TimerCallback callback;
        private readonly object? state;
        public TimeSpan Due { get; }
        public bool Disposed { get; private set; }
        public ControlledTimer(TimerCallback callback, object? state, TimeSpan due)
        { this.callback = callback; this.state = state; Due = due; }
        public void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { Disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
