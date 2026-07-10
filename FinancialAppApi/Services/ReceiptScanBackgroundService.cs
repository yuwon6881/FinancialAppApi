namespace FinancialAppApi.Services;

// Single consumer for the ReceiptScanQueue. Processes queued receipt-scan jobs one at
// a time so the fallback OCR path never runs more concurrent AI provider calls than a
// free-tier container can absorb. Each job gets its own DI scope (ReceiptScanProcessor
// is scoped and depends on the scoped AppDbContext).
public class ReceiptScanBackgroundService : BackgroundService
{
    private readonly ReceiptScanQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReceiptScanBackgroundService> _logger;

    public ReceiptScanBackgroundService(
        ReceiptScanQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ReceiptScanBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ReceiptScanProcessor>();
                await processor.ProcessAsync(jobId);
            }
            catch (Exception ex)
            {
                // A single failed job must not tear down the consumer loop.
                _logger.LogError(ex, "Fallback receipt scan processor failed for job {JobId}.", jobId);
            }
        }
    }
}
