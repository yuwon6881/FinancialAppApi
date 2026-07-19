using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class ReceiptScanJobCleanupServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PruneExpiredJobsAsync_DeletesExpiredJobsAndTheirObjects()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var jobs = new[]
        {
            NewJob("old-completed", "completed", Now.AddHours(-25)),
            NewJob("old-processing", "processing", Now.AddHours(-25)),
            NewJob("recent-queued", "queued", Now.AddHours(-1))
        };
        context.ReceiptScanJobs.AddRange(jobs);
        await context.SaveChangesAsync();
        var imageStore = new FakeReceiptImageStore();
        foreach (var job in jobs) imageStore.Objects.Add(job.StorageObjectPath!, [0xFF, 0xD8, 0xFF]);
        var service = NewService(context, imageStore);

        var result = await service.PruneExpiredJobsAsync();

        Assert.Equal(2, result.DeletedObjects);
        Assert.Equal(2, result.DeletedJobs);
        Assert.Null(await context.ReceiptScanJobs.FindAsync("old-completed"));
        Assert.Null(await context.ReceiptScanJobs.FindAsync("old-processing"));
        Assert.NotNull(await context.ReceiptScanJobs.FindAsync("recent-queued"));
        Assert.Single(imageStore.Objects);
        Assert.Contains("receipts/recent-queued.jpg", imageStore.Objects.Keys);
    }

    [Fact]
    public async Task PruneExpiredJobsAsync_DeletesTerminalObjectBeforeResultRetentionExpires()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var job = NewJob("recent-failed", "failed", Now.AddHours(-1));
        context.ReceiptScanJobs.Add(job);
        await context.SaveChangesAsync();
        var imageStore = new FakeReceiptImageStore();
        imageStore.Objects.Add(job.StorageObjectPath!, [0xFF, 0xD8, 0xFF]);
        var service = NewService(context, imageStore);

        var result = await service.PruneExpiredJobsAsync();

        Assert.Equal(1, result.DeletedObjects);
        Assert.Equal(0, result.DeletedJobs);
        Assert.Null((await context.ReceiptScanJobs.SingleAsync()).StorageObjectPath);
        Assert.Empty(imageStore.Objects);
    }

    [Fact]
    public async Task PruneExpiredJobsAsync_WhenObjectDeleteFails_RetainsExpiredJobForRetry()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.Add(NewJob("old-processing", "processing", Now.AddHours(-25)));
        await context.SaveChangesAsync();
        var service = NewService(context, new FakeReceiptImageStore { FailDeletes = true });

        var result = await service.PruneExpiredJobsAsync();

        Assert.Equal(0, result.DeletedJobs);
        Assert.NotNull(await context.ReceiptScanJobs.FindAsync("old-processing"));
    }

    private static ReceiptScanJobCleanupService NewService(
        Database.AppDbContext context,
        IReceiptImageStore imageStore)
    {
        var policy = new ReceiptScanRetentionPolicy(TestHelpers.NewConfiguration(
            ("Ocr:TerminalJobRetentionHours", "24"),
            ("Ocr:AbandonedJobRetentionHours", "24")));
        return new ReceiptScanJobCleanupService(
            context,
            policy,
            imageStore,
            NullLogger<ReceiptScanJobCleanupService>.Instance,
            new FixedTimeProvider(Now));
    }

    private static ReceiptScanJob NewJob(string id, string status, DateTimeOffset timestamp)
    {
        return new ReceiptScanJob
        {
            Id = id,
            Username = "alice",
            Status = status,
            MimeType = "image/jpeg",
            StorageObjectPath = $"receipts/{id}.jpg",
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
