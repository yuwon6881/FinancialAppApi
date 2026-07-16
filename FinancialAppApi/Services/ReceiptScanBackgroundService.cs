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
        // The non-Cloud-Tasks fallback queue is in memory, but its payload is durable in
        // Postgres. Recover queued jobs (and expired processing leases) whenever a new
        // container starts so an instance restart cannot strand an accepted scan forever.
        if (!HasCloudTasksConfig())
        {
            await RecoverPersistedJobsAsync(stoppingToken);
        }

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
