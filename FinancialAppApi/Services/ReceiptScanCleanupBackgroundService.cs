namespace FinancialAppApi.Services;

/// <summary>
/// Runs OCR retention at process startup and periodically while a Cloud Run instance
/// is alive. Scale-to-zero needs no special scheduler: no jobs can accumulate while
/// no instance is running, and the next instance prunes immediately on startup.
/// </summary>
public sealed class ReceiptScanCleanupBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ReceiptScanRetentionPolicy _retentionPolicy;
    private readonly ILogger<ReceiptScanCleanupBackgroundService> _logger;

    public ReceiptScanCleanupBackgroundService(
        IServiceScopeFactory scopeFactory,
        ReceiptScanRetentionPolicy retentionPolicy,
        ILogger<ReceiptScanCleanupBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _retentionPolicy = retentionPolicy;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var cleanup = scope.ServiceProvider.GetRequiredService<ReceiptScanJobCleanupService>();
                var result = await cleanup.PruneExpiredJobsAsync(stoppingToken);
                if (result.ScrubbedImages > 0 || result.DeletedJobs > 0)
                {
                    _logger.LogInformation(
                        "OCR retention scrubbed {ScrubbedImages} image payloads and deleted {DeletedJobs} expired jobs.",
                        result.ScrubbedImages,
                        result.DeletedJobs);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "OCR retention cleanup failed; it will retry on the next interval.");
            }

            try
            {
                await Task.Delay(_retentionPolicy.CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
