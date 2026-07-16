using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed record ReceiptScanCleanupResult(int ScrubbedImages, int DeletedJobs);

public sealed class ReceiptScanJobCleanupService
{
    private readonly AppDbContext _context;
    private readonly ReceiptScanRetentionPolicy _retentionPolicy;
    private readonly TimeProvider _timeProvider;

    public ReceiptScanJobCleanupService(
        AppDbContext context,
        ReceiptScanRetentionPolicy retentionPolicy,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _retentionPolicy = retentionPolicy;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReceiptScanCleanupResult> PruneExpiredJobsAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var terminalCutoff = now - _retentionPolicy.TerminalJobRetention;
        var abandonedCutoff = now - _retentionPolicy.AbandonedJobRetention;

        if (_context.Database.IsRelational())
        {
            // Defense in depth for legacy rows and interrupted terminal writes: receipt
            // images should never remain attached to a completed/failed job.
            var scrubbedImages = await _context.ReceiptScanJobs
                .Where(job =>
                    (job.Status == "completed" || job.Status == "failed") &&
                    job.ImageData != null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(job => job.ImageData, (byte[]?)null),
                    cancellationToken);

            var deletedTerminal = await _context.ReceiptScanJobs
                .Where(job =>
                    (job.Status == "completed" || job.Status == "failed") &&
                    job.UpdatedAt < terminalCutoff)
                .ExecuteDeleteAsync(cancellationToken);

            var deletedAbandoned = await _context.ReceiptScanJobs
                .Where(job =>
                    job.Status != "completed" &&
                    job.Status != "failed" &&
                    job.UpdatedAt < abandonedCutoff)
                .ExecuteDeleteAsync(cancellationToken);

            return new ReceiptScanCleanupResult(scrubbedImages, deletedTerminal + deletedAbandoned);
        }

        var jobs = await _context.ReceiptScanJobs.ToListAsync(cancellationToken);
        var scrubbed = 0;
        var expired = new List<Models.ReceiptScanJob>();
        foreach (var job in jobs)
        {
            var terminal = job.Status is "completed" or "failed";
            if (terminal && job.ImageData != null)
            {
                job.ImageData = null;
                scrubbed++;
            }

            var terminalExpired = terminal && job.UpdatedAt < terminalCutoff;
            var abandoned = !terminal && job.UpdatedAt < abandonedCutoff;
            if (terminalExpired || abandoned) expired.Add(job);
        }

        _context.ReceiptScanJobs.RemoveRange(expired);
        if (scrubbed > 0 || expired.Count > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        return new ReceiptScanCleanupResult(scrubbed, expired.Count);
    }
}
