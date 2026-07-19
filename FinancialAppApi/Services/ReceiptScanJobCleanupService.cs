using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed record ReceiptScanCleanupResult(
    int DeletedObjects,
    int DeletedJobs);

public sealed class ReceiptScanJobCleanupService
{
    private readonly AppDbContext _context;
    private readonly ReceiptScanRetentionPolicy _retentionPolicy;
    private readonly IReceiptImageStore _imageStore;
    private readonly ILogger<ReceiptScanJobCleanupService> _logger;
    private readonly TimeProvider _timeProvider;

    public ReceiptScanJobCleanupService(
        AppDbContext context,
        ReceiptScanRetentionPolicy retentionPolicy,
        IReceiptImageStore imageStore,
        ILogger<ReceiptScanJobCleanupService> logger,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _retentionPolicy = retentionPolicy;
        _imageStore = imageStore;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReceiptScanCleanupResult> PruneExpiredJobsAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var terminalCutoff = now - _retentionPolicy.TerminalJobRetention;
        var abandonedCutoff = now - _retentionPolicy.AbandonedJobRetention;

        // First scrub images from terminal jobs even while their small JSON result
        // remains available for idempotent client polling.
        var terminalPayloads = await _context.ReceiptScanJobs
            .Where(job =>
                (job.Status == "completed" || job.Status == "failed") &&
                job.StorageObjectPath != null)
            .Select(job => new TerminalPayload(
                job.Id,
                job.StorageObjectPath!))
            .ToListAsync(cancellationToken);

        var clearedPathIds = new List<string>();
        var deletedObjects = 0;
        foreach (var payload in terminalPayloads)
        {
            if (await TryDeleteObjectAsync(payload.Id, payload.StorageObjectPath, cancellationToken))
            {
                clearedPathIds.Add(payload.Id);
                deletedObjects++;
            }
        }

        if (clearedPathIds.Count > 0 && _context.Database.IsRelational())
        {
            await _context.ReceiptScanJobs
                .Where(job => clearedPathIds.Contains(job.Id))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(job => job.StorageObjectPath, (string?)null),
                    cancellationToken);
        }
        else if (clearedPathIds.Count > 0)
        {
            var clearedPathIdSet = clearedPathIds.ToHashSet(StringComparer.Ordinal);
            foreach (var payload in terminalPayloads)
            {
                var job = await _context.ReceiptScanJobs.FindAsync([payload.Id], cancellationToken);
                if (job == null) continue;
                if (clearedPathIdSet.Contains(payload.Id)) job.StorageObjectPath = null;
            }
            await _context.SaveChangesAsync(cancellationToken);
        }

        // Delete only jobs whose external object is already gone. If Storage is
        // temporarily unavailable, retain the row and path for the next retry.
        var expiredJobs = await _context.ReceiptScanJobs
            .Where(job =>
                ((job.Status == "completed" || job.Status == "failed") && job.UpdatedAt < terminalCutoff) ||
                (job.Status != "completed" && job.Status != "failed" && job.UpdatedAt < abandonedCutoff))
            .Select(job => new ExpiredJob(job.Id, job.StorageObjectPath))
            .ToListAsync(cancellationToken);

        var deletableJobIds = new List<string>();
        foreach (var job in expiredJobs)
        {
            if (job.StorageObjectPath == null ||
                await TryDeleteObjectAsync(job.Id, job.StorageObjectPath, cancellationToken))
            {
                if (job.StorageObjectPath != null) deletedObjects++;
                deletableJobIds.Add(job.Id);
            }
        }

        int deletedJobs;
        if (deletableJobIds.Count == 0)
        {
            deletedJobs = 0;
        }
        else if (_context.Database.IsRelational())
        {
            deletedJobs = await _context.ReceiptScanJobs
                .Where(job => deletableJobIds.Contains(job.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            var jobs = await _context.ReceiptScanJobs
                .Where(job => deletableJobIds.Contains(job.Id))
                .ToListAsync(cancellationToken);
            _context.ReceiptScanJobs.RemoveRange(jobs);
            await _context.SaveChangesAsync(cancellationToken);
            deletedJobs = jobs.Count;
        }

        return new ReceiptScanCleanupResult(deletedObjects, deletedJobs);
    }

    private async Task<bool> TryDeleteObjectAsync(
        string jobId,
        string objectPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await _imageStore.DeleteIfExistsAsync(objectPath, cancellationToken);
            return true;
        }
        catch (ReceiptImageStoreException exception)
        {
            _logger.LogError(exception, "Could not delete stored receipt image for OCR job {JobId}; cleanup will retry.", jobId);
            return false;
        }
    }

    private sealed record TerminalPayload(string Id, string StorageObjectPath);

    private sealed record ExpiredJob(string Id, string? StorageObjectPath);
}
