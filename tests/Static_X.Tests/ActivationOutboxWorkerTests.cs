using System.Threading.Channels;
using BLL.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Static_X.API.Integrations;
using Xunit;

namespace Static_X.Tests;

public sealed class ActivationOutboxWorkerTests
{
    [Theory]
    [InlineData("processed", 1)]
    [InlineData("idle", 10)]
    [InlineData("error", 30)]
    public async Task Worker_Iteration_DisposesScopeAndWaitsExpectedDelayBeforeRetry(string outcome, int seconds)
    {
        var state = new DispatchState { Outcome = outcome };
        var clock = new ControlledClock();
        var services = new ServiceCollection().AddSingleton(state).AddScoped<IActivationDispatcher, ScopedDispatcher>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var worker = new ActivationOutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<ActivationOutboxWorker>.Instance);
        await worker.StartAsync(default);
        var first = await state.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var timer = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(seconds), timer.DueTime);
        Assert.True(first.Disposed);
        Assert.Equal(1, state.Count);
        timer.Fire();
        var second = await state.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var nextTimer = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(first, second);
        Assert.True(second.Disposed);
        Assert.Equal(TimeSpan.FromSeconds(seconds), nextTimer.DueTime);
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(nextTimer.Disposed);
        Assert.Equal(2, state.Count);
    }

    [Fact]
    public async Task Worker_StopDuringDispatch_ForwardsCancellationAndDisposesScopeWithoutRetry()
    {
        var state = new DispatchState { Outcome = "wait" };
        var clock = new ControlledClock();
        await using var provider = new ServiceCollection().AddSingleton(state)
            .AddScoped<IActivationDispatcher, ScopedDispatcher>().BuildServiceProvider();
        using var worker = new ActivationOutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<ActivationOutboxWorker>.Instance);
        await worker.StartAsync(default);
        var dispatch = await state.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(dispatch.Token.CanBeCanceled);
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(dispatch.Token.IsCancellationRequested);
        Assert.True(dispatch.Disposed);
        Assert.Equal(1, state.Count);
        Assert.False(clock.Timers.Reader.TryRead(out _));
    }

    private sealed class DispatchState
    {
        public string Outcome { get; init; } = "idle";
        public int Count;
        public Channel<ScopedDispatcher> Calls { get; } = Channel.CreateUnbounded<ScopedDispatcher>();
    }

    private sealed class ScopedDispatcher : IActivationDispatcher, IAsyncDisposable
    {
        private readonly DispatchState state;
        public bool Disposed { get; private set; }
        public CancellationToken Token { get; private set; }

        public ScopedDispatcher(DispatchState state) { this.state = state; }

        public async Task<bool> DispatchOneAsync(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Interlocked.Increment(ref state.Count);
            state.Calls.Writer.TryWrite(this);
            if (state.Outcome == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (state.Outcome == "error") throw new InvalidOperationException("Private provider detail must not be logged.");
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
        public TimeSpan DueTime { get; }
        public bool Disposed { get; private set; }
        public ControlledTimer(TimerCallback callback, object? state, TimeSpan dueTime)
        { this.callback = callback; this.state = state; DueTime = dueTime; }
        public void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { Disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
