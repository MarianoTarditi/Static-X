using BLL.Services.Interfaces;

namespace Static_X.API.Integrations;

/// <summary>Dispatches recovery mail in fresh scopes with bounded cancellation-aware delays.</summary>
public sealed class PasswordRecoveryOutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider clock;
    private readonly ILogger<PasswordRecoveryOutboxWorker> logger;

    public PasswordRecoveryOutboxWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PasswordRecoveryOutboxWorker> logger)
    {
        this.scopes = scopes;
        this.clock = clock;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(10);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IPasswordRecoveryDispatcher>();
                await dispatcher.CleanupAsync(stoppingToken);
                if (await dispatcher.DispatchOneAsync(stoppingToken))
                    delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                logger.LogWarning("Password recovery delivery unavailable. Verify configuration and dependencies. Retrying in 30 seconds.");
                delay = TimeSpan.FromSeconds(30);
            }
            try { await Task.Delay(delay, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
