using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class ReceiptScanProcessorTests
{
    [Theory]
    [InlineData("processing")]
    [InlineData("completed")]
    [InlineData("failed")]
    public async Task ProcessAsync_DoesNotProcessAnAlreadyClaimedJob(string status)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var imageStore = new FakeReceiptImageStore();
        imageStore.Objects.Add("receipts/scan-1.jpg", "unchanged"u8.ToArray());
        context.ReceiptScanJobs.Add(new ReceiptScanJob
        {
            Id = "scan-1",
            Username = "alice",
            Status = status,
            StorageObjectPath = "receipts/scan-1.jpg",
            MimeType = "image/jpeg"
        });
        await context.SaveChangesAsync();
        var configuration = TestHelpers.NewConfiguration();
        var aiClient = new AiClient(new HttpClient(), configuration, NullLogger<AiClient>.Instance);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var processor = new ReceiptScanProcessor(
            aiClient,
            context,
            imageStore,
            new TransactionCategoryService(context, cache),
            NullLogger<ReceiptScanProcessor>.Instance);

        var result = await processor.ProcessAsync("scan-1");

        var job = context.ReceiptScanJobs.Single();
        Assert.Equal(
            status == "processing" ? ReceiptScanProcessStatus.InProgress : ReceiptScanProcessStatus.AlreadyFinished,
            result);
        Assert.Equal(status, job.Status);
        Assert.Equal("receipts/scan-1.jpg", job.StorageObjectPath);
        Assert.Equal("unchanged"u8.ToArray(), imageStore.Objects["receipts/scan-1.jpg"]);
    }

    [Fact]
    public async Task ProcessAsync_ReclaimsAStaleProcessingJob()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var imageStore = new FakeReceiptImageStore();
        imageStore.Objects.Add("receipts/scan-1.jpg", "receipt-image"u8.ToArray());
        context.ReceiptScanJobs.Add(new ReceiptScanJob
        {
            Id = "scan-1",
            Username = "alice",
            Status = "processing",
            StorageObjectPath = "receipts/scan-1.jpg",
            MimeType = "image/jpeg",
            UpdatedAt = DateTime.UtcNow - ReceiptScanProcessor.ProcessingLease - TimeSpan.FromMinutes(1)
        });
        await context.SaveChangesAsync();
        var configuration = TestHelpers.NewConfiguration();
        var aiClient = new AiClient(new HttpClient(), configuration, NullLogger<AiClient>.Instance);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var processor = new ReceiptScanProcessor(
            aiClient,
            context,
            imageStore,
            new TransactionCategoryService(context, cache),
            NullLogger<ReceiptScanProcessor>.Instance);

        var result = await processor.ProcessAsync("scan-1");

        var job = context.ReceiptScanJobs.Single();
        Assert.Equal(ReceiptScanProcessStatus.Processed, result);
        Assert.Equal("failed", job.Status);
        Assert.Null(job.StorageObjectPath);
        Assert.Empty(imageStore.Objects);
        Assert.Contains("not configured", job.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAsync_WhenStorageDownloadFails_RequeuesForRetry()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.Add(new ReceiptScanJob
        {
            Id = "scan-1",
            Username = "alice",
            Status = "queued",
            StorageObjectPath = "receipts/scan-1.jpg",
            MimeType = "image/jpeg"
        });
        await context.SaveChangesAsync();
        var configuration = TestHelpers.NewConfiguration();
        var imageStore = new FakeReceiptImageStore { FailDownloads = true };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var processor = new ReceiptScanProcessor(
            new AiClient(new HttpClient(), configuration, NullLogger<AiClient>.Instance),
            context,
            imageStore,
            new TransactionCategoryService(context, cache),
            NullLogger<ReceiptScanProcessor>.Instance);

        await Assert.ThrowsAsync<ReceiptImageStoreException>(() => processor.ProcessAsync("scan-1"));

        Assert.Equal("queued", context.ReceiptScanJobs.Single().Status);
        Assert.Equal("receipts/scan-1.jpg", context.ReceiptScanJobs.Single().StorageObjectPath);
    }
}
