namespace FinancialAppApi.Services;

/// <summary>
/// Runs ledger retention at process startup and periodically while a Cloud Run instance is alive,
/// mirroring <see cref="ReceiptScanCleanupBackgroundService"/>. Scale-to-zero needs no scheduler:
/// nothing accumulates while no instance runs, and the next instance prunes on startup.
/// </summary>
public sealed class LedgerRetentionBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LedgerRetentionPolicy _policy;
    private readonly ILogger<LedgerRetentionBackgroundService> _logger;

    public LedgerRetentionBackgroundService(
        IServiceScopeFactory scopeFactory,
        LedgerRetentionPolicy policy,
        ILogger<LedgerRetentionBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _policy = policy;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_policy.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A scope per work item: this service is a singleton and must never hold a
                // scoped AppDbContext of its own.
                using var scope = _scopeFactory.CreateScope();
                var retention = scope.ServiceProvider.GetRequiredService<LedgerRetentionService>();
                var result = await retention.PruneAsync(stoppingToken);
                if (result.Total > 0)
                {
                    _logger.LogInformation(
                        "Ledger retention deleted {PriceBars} price bars and {Notifications} notification records.",
                        result.PriceBars,
                        result.Total - result.PriceBars);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Ledger retention failed; it will retry on the next interval.");
            }

            try
            {
                await Task.Delay(_policy.CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
