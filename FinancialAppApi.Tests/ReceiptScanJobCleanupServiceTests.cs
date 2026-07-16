using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class ReceiptScanJobCleanupServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PruneExpiredJobsAsync_DeletesExpiredTerminalAndAbandonedJobs()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.AddRange(
            NewJob("old-completed", "completed", Now.AddHours(-25)),
            NewJob("old-processing", "processing", Now.AddHours(-25)),
            NewJob("recent-queued", "queued", Now.AddHours(-1)));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.PruneExpiredJobsAsync();

        Assert.Equal(2, result.DeletedJobs);
        Assert.Null(await context.ReceiptScanJobs.FindAsync("old-completed"));
        Assert.Null(await context.ReceiptScanJobs.FindAsync("old-processing"));
        Assert.NotNull(await context.ReceiptScanJobs.FindAsync("recent-queued"));
    }

    [Fact]
    public async Task PruneExpiredJobsAsync_ScrubsTerminalImageBeforeRetentionExpires()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.Add(NewJob("recent-failed", "failed", Now.AddHours(-1)));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.PruneExpiredJobsAsync();

        Assert.Equal(1, result.ScrubbedImages);
        Assert.Equal(0, result.DeletedJobs);
        Assert.Null((await context.ReceiptScanJobs.SingleAsync()).ImageData);
    }

    private static ReceiptScanJobCleanupService NewService(Database.AppDbContext context)
    {
        var policy = new ReceiptScanRetentionPolicy(TestHelpers.NewConfiguration(
            ("Ocr:TerminalJobRetentionHours", "24"),
            ("Ocr:AbandonedJobRetentionHours", "24")));
        return new ReceiptScanJobCleanupService(context, policy, new FixedTimeProvider(Now));
    }

    private static ReceiptScanJob NewJob(string id, string status, DateTimeOffset timestamp)
    {
        return new ReceiptScanJob
        {
            Id = id,
            Username = "alice",
            Status = status,
            MimeType = "image/jpeg",
            ImageData = [0xFF, 0xD8, 0xFF],
            CreatedAt = timestamp.UtcDateTime,
            UpdatedAt = timestamp.UtcDateTime,
            CompletedAt = status is "completed" or "failed" ? timestamp.UtcDateTime : null
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
