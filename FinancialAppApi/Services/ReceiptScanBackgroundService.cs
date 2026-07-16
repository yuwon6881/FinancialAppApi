using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

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
    private readonly IConfiguration _configuration;

    public ReceiptScanBackgroundService(
        ReceiptScanQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ReceiptScanBackgroundService> logger,
        IConfiguration configuration)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HasCloudTasksConfig())
        {
            // Cloud Tasks invokes the HTTP worker; the local channel is intentionally unused.
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            return;
        }

        // Run recovery alongside the single consumer. Polling once per lease also recovers
        // a job whose processor failed without requiring the whole container to restart.
        await Task.WhenAll(
            ProcessQueueAsync(stoppingToken),
            RecoverPersistedJobsLoopAsync(stoppingToken));
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
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

    private async Task RecoverPersistedJobsLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverPersistedJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not recover persisted receipt scan jobs; retrying after the lease interval.");
            }

            try
            {
                await Task.Delay(ReceiptScanProcessor.ProcessingLease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RecoverPersistedJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var staleBefore = DateTime.UtcNow - ReceiptScanProcessor.ProcessingLease;
        var jobIds = await context.ReceiptScanJobs
            .AsNoTracking()
            .Where(job => job.Status == "queued" ||
                (job.Status == "processing" && job.UpdatedAt < staleBefore))
            .OrderBy(job => job.CreatedAt)
            .Select(job => job.Id)
            .ToListAsync(cancellationToken);

        foreach (var jobId in jobIds)
        {
            await _queue.EnqueueAsync(jobId, cancellationToken);
        }
    }

    private bool HasCloudTasksConfig() =>
        !string.IsNullOrWhiteSpace(_configuration["CloudTasks:ProjectId"]) &&
        !string.IsNullOrWhiteSpace(_configuration["CloudTasks:LocationId"]) &&
        !string.IsNullOrWhiteSpace(_configuration["CloudTasks:QueueId"]) &&
        !string.IsNullOrWhiteSpace(_configuration["CloudTasks:WorkerBaseUrl"]) &&
        !string.IsNullOrWhiteSpace(_configuration["OcrWorkerKey"]);
}
