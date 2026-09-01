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

    [Fact]
    public async Task ProcessAsync_ReceiptScanExcludesReservedCategoriesAndNeverReturnsIncome()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "alice", PasswordHash = "hash" });
        foreach (var name in new[] { "Adjustment", "Food", "Other", "Transfer" })
        {
            context.TransactionCategories.Add(new TransactionCategory
            {
                Id = name.ToLowerInvariant(), UserId = "test-user", Name = name,
            });
        }
        var imageStore = new FakeReceiptImageStore();
        imageStore.Objects.Add("receipts/scan-1.jpg", "receipt-image"u8.ToArray());
        context.ReceiptScanJobs.Add(new ReceiptScanJob
        {
            Id = "scan-1", UserId = "test-user", Username = "alice", Status = "queued",
            StorageObjectPath = "receipts/scan-1.jpg", MimeType = "image/jpeg",
        });
        await context.SaveChangesAsync();

        string? requestJson = null;
        var modelJson = """
            {"description":"Lunch","amount":12,"date":null,"category":"Transfer","ledgerCategory":"Income","confidence":0.9}
            """;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return SuccessResponse(modelJson);
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var processor = new ReceiptScanProcessor(
            new AiClient(
                new HttpClient(handler),
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            imageStore,
            new TransactionCategoryService(context, cache),
            NullLogger<ReceiptScanProcessor>.Instance);

        Assert.Equal(ReceiptScanProcessStatus.Processed, await processor.ProcessAsync("scan-1"));

        using var request = JsonDocument.Parse(requestJson!);
        var categoryEnum = request.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema")
            .GetProperty("properties").GetProperty("category").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).ToList();
        Assert.DoesNotContain("Transfer", categoryEnum);
        Assert.DoesNotContain("Adjustment", categoryEnum);
        using var result = JsonDocument.Parse(context.ReceiptScanJobs.Single().ResultJson!);
        Assert.Equal("Other", result.RootElement.GetProperty("category").GetString());
        Assert.Equal("Essentials", result.RootElement.GetProperty("ledgerCategory").GetString());
    }

    [Fact]
    public async Task ProcessAsync_InvestmentScanSendsSelectableContextWithEnoughOutputBudget()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var accountId = Guid.NewGuid();
        var instrumentId = Guid.NewGuid();
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "alice", PasswordHash = "hash" });
        context.InvestmentAccounts.Add(new InvestmentAccount
        {
            Id = accountId, UserId = "test-user", Name = "Moomoo MY", BaseCurrency = "MYR",
        });
        context.InvestmentInstruments.Add(new InvestmentInstrument
        {
            Id = instrumentId, UserId = "test-user", Symbol = "VOO", Name = "Vanguard S&P 500 ETF",
            Type = "ETF", Currency = "USD", Exchange = "NASDAQ",
        });
        var imageStore = new FakeReceiptImageStore();
        imageStore.Objects.Add("receipts/scan-1.jpg", "investment-image"u8.ToArray());
        context.ReceiptScanJobs.Add(new ReceiptScanJob
        {
            Id = "scan-1", UserId = "test-user", Username = "alice", Status = "queued", ScanType = "investment",
            StorageObjectPath = "receipts/scan-1.jpg", MimeType = "image/jpeg",
        });
        await context.SaveChangesAsync();

        string? requestJson = null;
        var modelJson = $$"""
            {"type":"Buy","accountId":"{{accountId}}","instrumentId":"{{instrumentId}}","tradeDate":"2026-01-01","units":2,"unitPrice":100,"cashAmount":200,"fees":1.25,"taxes":0.75,"currency":"USD","toCurrency":null,"toAmount":null,"confidence":0.9}
            """;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return SuccessResponse(modelJson);
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var processor = new ReceiptScanProcessor(
            new AiClient(
                new HttpClient(handler),
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            imageStore,
            new TransactionCategoryService(context, cache),
            NullLogger<ReceiptScanProcessor>.Instance);

        Assert.Equal(ReceiptScanProcessStatus.Processed, await processor.ProcessAsync("scan-1"));

        using var request = JsonDocument.Parse(requestJson!);
        Assert.True(request.RootElement.GetProperty("max_output_tokens").GetInt32() >= 800);
        var userText = request.RootElement.GetProperty("input")[0].GetProperty("content")
            .EnumerateArray().Where(part => part.GetProperty("type").GetString() == "input_text")
            .Select(part => part.GetProperty("text").GetString()).Single();
        const string prefix = "Available options JSON: ";
        Assert.StartsWith(prefix, userText);
        using var options = JsonDocument.Parse(userText![prefix.Length..]);
        var account = Assert.Single(options.RootElement.GetProperty("accounts").EnumerateArray());
        Assert.Equal(accountId, account.GetProperty("id").GetGuid());
        Assert.Equal("Moomoo MY", account.GetProperty("name").GetString());
        var instrument = Assert.Single(options.RootElement.GetProperty("instruments").EnumerateArray());
        Assert.Equal(instrumentId, instrument.GetProperty("id").GetGuid());
        Assert.Equal("VOO", instrument.GetProperty("symbol").GetString());
        Assert.Equal("Vanguard S&P 500 ETF", instrument.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ProcessAsync_WhenASplitScanIsCappedItReportsTruncationAndLostChargeScope()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "alice", PasswordHash = "hash" });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", UserId = "test-user", Name = "Food" });
        var imageStore = new FakeReceiptImageStore();
        imageStore.Objects.Add("receipts/scan-1.jpg", "receipt-image"u8.ToArray());
        context.ReceiptScanJobs.Add(new ReceiptScanJob
        {
            Id = "scan-1",
            UserId = "test-user",
            Username = "alice",
            Status = "queued",
            ScanType = "receipt-split",
            StorageObjectPath = "receipts/scan-1.jpg",
            MimeType = "image/jpeg",
        });
        await context.SaveChangesAsync();

        // 90 lines is past the 80-line extraction cap, and the service charge points at a line
        // that the cap removes.
        var items = string.Join(',', Enumerable.Range(0, 90).Select(index =>
            $"{{\"name\":\"Item {index}\",\"quantity\":1,\"unitPrice\":1,\"lineTotal\":1,\"confidence\":1}}"));
        var modelJson = $$"""
            {
              "description": "Big Table",
              "date": null, "currency": "MYR", "subtotal": 90, "total": 95,
              "category": "Food", "ledgerCategory": "Essentials",
              "items": [{{items}}],
              "charges": [{"label":"Service","kind":"service","operation":"add","basis":"subtotal","amount":5,"ratePercent":null,"sequence":0,"eligibleItemIndexes":[85],"confidence":1}],
              "fieldConfidence": {"description":1,"date":0,"currency":1,"subtotal":1,"total":1},
              "truncated": false, "warnings": [], "confidence": 0.9
            }
            """;
        var handler = new DelegateHandler(_ => SuccessResponse(modelJson));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var processor = new ReceiptScanProcessor(
            new AiClient(
                new HttpClient(handler),
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            imageStore,
            new TransactionCategoryService(context, cache),
            NullLogger<ReceiptScanProcessor>.Instance);

        Assert.Equal(ReceiptScanProcessStatus.Processed, await processor.ProcessAsync("scan-1"));

        var job = context.ReceiptScanJobs.Single();
        Assert.Equal("completed", job.Status);
        using var result = JsonDocument.Parse(job.ResultJson!);
        Assert.Equal(80, result.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(result.RootElement.GetProperty("truncated").GetBoolean());
        var warnings = result.RootElement.GetProperty("warnings")
            .EnumerateArray()
            .Select(warning => warning.GetString() ?? string.Empty)
            .ToList();
        Assert.Contains(warnings, warning => warning.Contains("first 80 lines"));
        Assert.Contains(warnings, warning => warning.Contains("spread over every line"));
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
