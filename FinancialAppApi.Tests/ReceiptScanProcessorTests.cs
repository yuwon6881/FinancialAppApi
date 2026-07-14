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
            ImageBase64 = "unchanged",
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

        await processor.ProcessAsync("scan-1");

        var job = context.ReceiptScanJobs.Single();
        Assert.Equal(status, job.Status);
        Assert.Equal("unchanged", job.ImageBase64);
    }
}
