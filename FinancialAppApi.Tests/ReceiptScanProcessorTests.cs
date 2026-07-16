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
        context.ReceiptScanJobs.Add(new ReceiptScanJob
        {
            Id = "scan-1",
            Username = "alice",
            Status = status,
            ImageData = "unchanged"u8.ToArray(),
            MimeType = "image/jpeg"
        });
        await context.SaveChangesAsync();
        var configuration = TestHelpers.NewConfiguration();
        var aiClient = new AiClient(new HttpClient(), configuration, NullLogger<AiClient>.Instance);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var processor = new ReceiptScanProcessor(
            aiClient,
            context,
            new TransactionCategoryService(context, cache),
            NullLogger<ReceiptScanProcessor>.Instance);

        var result = await processor.ProcessAsync("scan-1");

        var job = context.ReceiptScanJobs.Single();
        Assert.Equal(
            status == "processing" ? ReceiptScanProcessStatus.InProgress : ReceiptScanProcessStatus.AlreadyFinished,
            result);
        Assert.Equal(status, job.Status);
        Assert.Equal("unchanged"u8.ToArray(), job.ImageData);
    }

    [Fact]
    public async Task ProcessAsync_ReclaimsAStaleProcessingJob()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.Add(new ReceiptScanJob
        {
            Id = "scan-1",
            Username = "alice",
            Status = "processing",
            ImageData = "receipt-image"u8.ToArray(),
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
            new TransactionCategoryService(context, cache),
            NullLogger<ReceiptScanProcessor>.Instance);

        var result = await processor.ProcessAsync("scan-1");

        var job = context.ReceiptScanJobs.Single();
        Assert.Equal(ReceiptScanProcessStatus.Processed, result);
        Assert.Equal("failed", job.Status);
        Assert.Null(job.ImageData);
        Assert.Contains("not configured", job.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
