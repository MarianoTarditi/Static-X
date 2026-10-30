using BLL.Services.Interfaces;

namespace Static_X.API.Integrations;

/// <summary>Dispatches persisted invitations in isolated scopes with cancellation-aware backoff.</summary>
public sealed class ActivationOutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider clock;
    private readonly ILogger<ActivationOutboxWorker> logger;

    public ActivationOutboxWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ActivationOutboxWorker> logger)
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
                if (await scope.ServiceProvider.GetRequiredService<IActivationDispatcher>().DispatchOneAsync(stoppingToken))
                    delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Never include exceptions, recipients, URLs or provider responses in logs.
                logger.LogWarning("Activation delivery unavailable. Verify activation configuration, durable keys, database and provider access. Retrying in 30 seconds.");
                delay = TimeSpan.FromSeconds(30);
            }
            try { await Task.Delay(delay, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
