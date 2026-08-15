using System.Net;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public async Task ProcessAsync_WhenModelReturnsMalformedJson_FailsTheJobAndCleansUpTheImage()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "alice", PasswordHash = "hash" });
        context.TransactionCategories.Add(new TransactionCategory
        {
            Id = "food",
            UserId = "test-user",
            Name = "Food",
        });
        var imageStore = new FakeReceiptImageStore();
        imageStore.Objects.Add("receipts/scan-1.jpg", "receipt-image"u8.ToArray());
        context.ReceiptScanJobs.Add(new ReceiptScanJob
        {
            Id = "scan-1",
            UserId = "test-user",
            Username = "alice",
            Status = "queued",
            StorageObjectPath = "receipts/scan-1.jpg",
            MimeType = "image/jpeg",
        });
        await context.SaveChangesAsync();

        var handler = new DelegateHandler(_ => SuccessResponse("not-json"));
        var aiClient = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
            NullLogger<AiClient>.Instance);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var processor = new ReceiptScanProcessor(
            aiClient,
            context,
            imageStore,
            new TransactionCategoryService(context, cache),
            NullLogger<ReceiptScanProcessor>.Instance);

        var result = await processor.ProcessAsync("scan-1");

        Assert.Equal(ReceiptScanProcessStatus.Processed, result);
        var job = context.ReceiptScanJobs.Single();
        Assert.Equal("failed", job.Status);
        Assert.Contains("Could not read the receipt", job.ErrorMessage);
        Assert.Null(job.StorageObjectPath);
        Assert.Empty(imageStore.Objects);
    }

    [Fact]
    public async Task ProcessAsync_WhenCancelledDuringModelCall_LeavesTheClaimedJobForLeaseRecovery()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "alice", PasswordHash = "hash" });
        context.TransactionCategories.Add(new TransactionCategory
        {
            Id = "food",
            UserId = "test-user",
            Name = "Food",
        });
        var imageStore = new FakeReceiptImageStore();
        imageStore.Objects.Add("receipts/scan-1.jpg", "receipt-image"u8.ToArray());
        context.ReceiptScanJobs.Add(new ReceiptScanJob
        {
            Id = "scan-1",
            UserId = "test-user",
            Username = "alice",
            Status = "queued",
            StorageObjectPath = "receipts/scan-1.jpg",
            MimeType = "image/jpeg",
        });
        await context.SaveChangesAsync();

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (_, ct) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return SuccessResponse("never reached");
        });
        var aiClient = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
            NullLogger<AiClient>.Instance);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var processor = new ReceiptScanProcessor(
            aiClient,
            context,
            imageStore,
            new TransactionCategoryService(context, cache),
            NullLogger<ReceiptScanProcessor>.Instance);
        using var cancellation = new CancellationTokenSource();

        var processing = processor.ProcessAsync("scan-1", cancellation.Token);
        await requestStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        var job = context.ReceiptScanJobs.Single();
        Assert.Equal("processing", job.Status);
        Assert.Equal("receipts/scan-1.jpg", job.StorageObjectPath);
        Assert.NotEmpty(imageStore.Objects);
    }

    private static HttpResponseMessage SuccessResponse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""
            {
              "status": "completed",
              "output": [{ "type": "message", "content": [{ "type": "output_text", "text": {{JsonSerializer.Serialize(text)}} }] }]
            }
            """,
            Encoding.UTF8,
            "application/json"),
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this((request, _) => Task.FromResult(handler(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
