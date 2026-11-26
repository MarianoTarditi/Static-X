using BLL.DTOs.Students;
using BLL.Services.Interfaces;
using Entity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Static_X.API.Integrations.VideoLibrary;
using Xunit;

namespace Static_X.Tests;

public sealed class VideoCleanupWorkerTests
{
    [Fact]
    public async Task DisabledByDefault_DoesNotResolvePersistenceOrStorage()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var worker = new VideoCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(), NullLogger<VideoCleanupWorker>.Instance);
        await worker.StartAsync(default);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledWorker_UsesScopedServiceAndStopsOnCancellationEvenAfterFailure(bool fail)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0; var disposed = 0;
        using var provider = new ServiceCollection().AddScoped<IVideoLibraryService>(_ => new CleanupService(() =>
        {
            calls++;
            cancellation.Cancel();
            if (fail) throw new IOException("Injected cleanup failure.");
        }, () => disposed++)).BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["VideoStorage:CleanupEnabled"] = "true" }).Build();
        using var worker = new VideoCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(), configuration, NullLogger<VideoCleanupWorker>.Instance);
        await worker.StartAsync(cancellation.Token);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Equal(1, disposed);
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
    }

    private sealed class CleanupService : IVideoLibraryService, IDisposable
    {
        private readonly Action cleanup;
        private readonly Action dispose;
        public CleanupService(Action cleanup, Action dispose) { this.cleanup = cleanup; this.dispose = dispose; }
        public Task<bool> CleanupOneAsync(CancellationToken cancellationToken) { cleanup(); return Task.FromResult(false); }
        public void Dispose() => dispose();
        public Task<VideoListResponse> ListAsync(int gymId, AccountRole role, VideoListRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VideoResponse> UploadAsync(int gymId, AccountRole role, string actorId, CreateVideoRequest request, Stream content, long length, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VideoResponse> ReplaceAsync(int gymId, AccountRole role, string actorId, Guid videoId, Guid expectedRevision, Stream content, long length, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(int gymId, AccountRole role, string actorId, Guid videoId, Guid expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VideoContentResponse> ReadAsync(int gymId, AccountRole role, Guid videoId, VideoRangeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
