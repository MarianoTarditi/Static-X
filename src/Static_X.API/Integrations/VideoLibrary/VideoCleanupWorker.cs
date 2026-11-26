using BLL.Services.Interfaces;

namespace Static_X.API.Integrations.VideoLibrary;

/// <summary>Retries durable video cleanup with fresh scopes and bounded, cancellable delays.</summary>
public sealed class VideoCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly IConfiguration configuration;
    private readonly ILogger<VideoCleanupWorker> logger;

    public VideoCleanupWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<VideoCleanupWorker> logger)
    { this.scopes = scopes; this.configuration = configuration; this.logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Opt in only after the prepared migration and private storage have been provisioned by an operator.
        if (!configuration.GetValue<bool>("VideoStorage:CleanupEnabled")) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                for (var i = 0; i < 20; i++)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (!await scope.ServiceProvider.GetRequiredService<IVideoLibraryService>().CleanupOneAsync(stoppingToken)) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Video cleanup failed; durable work will be retried."); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
