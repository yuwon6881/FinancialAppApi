using System.Net;
using System.Text;
using System.Text.Json;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class AiAssistantHistoricalContextTests
{
    [Fact]
    public async Task ChatAsync_NamedOldCycleUsesThatCyclesCompleteDataInsteadOfNewestRows()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            HideSensitive = false,
            Currency = "USD"
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.AddRange(
            Transaction("old", new DateTime(2023, 3, 15, 12, 0, 0, DateTimeKind.Utc), "Old Cycle Coffee", -42),
            Transaction("new", new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc), "Newest Lunch", -99));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var aiClient = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")),
            NullLogger<AiClient>.Instance);
        var service = new AiAssistantService(aiClient, context, new TransactionCategoryService(context, cache));

        var outcome = await service.ChatAsync(new AiChatRequest("How much did I spend in the March 2023 cycle?", []));

        Assert.False(outcome.IsProviderError);
        Assert.Contains("Old Cycle Coffee", handler.UserContent);
        Assert.DoesNotContain("Newest Lunch", handler.UserContent);
        Assert.Contains("\"month\":\"Mar\"", handler.UserContent);
        Assert.Contains("\"outflow\":42", handler.UserContent);
        Assert.Contains("\"aggregatesCoverAllTransactionsInRequestedCycles\":true", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_EditInPreviousCycleResolvesRecordOutsideActiveCycle()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            HideSensitive = false
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.Add(Transaction(
            "previous-record",
            new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc),
            "Old Cafe",
            -18));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var aiClient = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")),
            NullLogger<AiClient>.Instance);
        var service = new AiAssistantService(aiClient, context, new TransactionCategoryService(context, cache));

        var outcome = await service.ChatAsync(new AiChatRequest("Edit Old Cafe in the previous cycle and change amount to 25", []));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("openEditLedgerDraft", action.Type);
        Assert.Equal("previous-record", action.Payload["id"]);
        var changes = Assert.IsType<Dictionary<string, object?>>(action.Payload["changes"]);
        Assert.Equal(25m, changes["amount"]);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ChatAsync_PreviousCycleQuestionDoesNotMixInActiveCycle()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            HideSensitive = false
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.AddRange(
            Transaction("previous", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), "Previous Cycle Item", -12),
            Transaction("active", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Active Cycle Item", -30));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            new AiClient(
                new HttpClient(handler),
                TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("What transactions were in the previous cycle?", []));

        Assert.Contains("Previous Cycle Item", handler.UserContent);
        Assert.DoesNotContain("Active Cycle Item", handler.UserContent);
        Assert.Contains("\"month\":\"Jun\"", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_TargetCycleAggregatesMoreThanFiveHundredTransactions()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            HideSensitive = false
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.AddRange(Enumerable.Range(1, 510).Select(index => Transaction(
            $"old-{index}",
            new DateTime(2023, 3, (index % 28) + 1, 12, 0, 0, DateTimeKind.Utc),
            $"Historical Item {index}",
            -1)));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            new AiClient(
                new HttpClient(handler),
                TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("How much did I spend in March 2023?", []));

        Assert.Contains("\"transactionCount\":510", handler.UserContent);
        Assert.Contains("\"outflow\":510", handler.UserContent);
        // A pure aggregate question is answered from cycleSummaries alone, so the bounded
        // per-row detail block is omitted even though the aggregates still cover all 510 rows.
        Assert.Contains("\"detailedTransactionsIncluded\":0", handler.UserContent);
        Assert.Contains("\"recentTransactions\":[]", handler.UserContent);
        Assert.Contains("\"detailedTransactionsTotalInScope\":510", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_AggregateQuestionOmitsDetailButKeepsCycleSummary()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            HideSensitive = false
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.Add(Transaction("mar", new DateTime(2023, 3, 12, 12, 0, 0, DateTimeKind.Utc), "March Coffee", -15));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            new AiClient(
                new HttpClient(handler),
                TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("How much did I spend on food in March 2023?", []));

        // Aggregate answer lives in the cycle summary...
        Assert.Contains("\"categorySpend\"", handler.UserContent);
        Assert.Contains("\"outflow\":15", handler.UserContent);
        // ...so the detail sample is not sent for a question that never asked for individual rows.
        Assert.Contains("\"recentTransactions\":[]", handler.UserContent);
        Assert.Contains("\"detailedTransactionsIncluded\":0", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_DetailQuestionStillIncludesTransactionRows()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            HideSensitive = false
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.Add(Transaction("mar", new DateTime(2023, 3, 12, 12, 0, 0, DateTimeKind.Utc), "March Coffee", -15));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            new AiClient(
                new HttpClient(handler),
                TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("Find my coffee transactions in March 2023", []));

        // "find"/"transactions" is a detail signal, so the per-row sample is included.
        Assert.DoesNotContain("\"recentTransactions\":[]", handler.UserContent);
        Assert.Contains("March Coffee", handler.UserContent);
        Assert.Contains("\"detailedTransactionsIncluded\":1", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_ImprovementQuestionIncludesBudgetTargets()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            HideSensitive = false,
            EssentialsAlloc = 0.5m,
            GrowthAlloc = 0.25m,
            StabilityAlloc = 0.15m,
            RewardsAlloc = 0.1m
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.Add(Transaction("active", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            new AiClient(
                new HttpClient(handler),
                TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("How can I improve my spending this cycle?", []));

        Assert.Contains("\"budgetTargets\"", handler.UserContent);
        Assert.Contains("\"essentials\":0.5", handler.UserContent);
        Assert.Contains("\"stability\":0.15", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_SensitiveModeOmitsBudgetTargets()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            HideSensitive = true,
            EssentialsAlloc = 0.5m
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.Add(Transaction("active", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            new AiClient(
                new HttpClient(handler),
                TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("How can I improve my spending this cycle?", []));

        // budgetTargets are amount-oriented guidance the app withholds in sensitive mode, and
        // WhenWritingNull keeps the key out of the prompt entirely rather than sending null.
        Assert.DoesNotContain("budgetTargets", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_ExplicitCycleRangeIncludesIntermediateCycles()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            HideSensitive = false
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.Add(Transaction(
            "april",
            new DateTime(2023, 4, 10, 12, 0, 0, DateTimeKind.Utc),
            "April Cycle Item",
            -20));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            new AiClient(
                new HttpClient(handler),
                TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("Compare transactions from March 2023 to May 2023", []));

        Assert.Contains("April Cycle Item", handler.UserContent);
        Assert.Contains("\"month\":\"Mar\"", handler.UserContent);
        Assert.Contains("\"month\":\"Apr\"", handler.UserContent);
        Assert.Contains("\"month\":\"May\"", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_ExactOldDateWithOneRecordCanEditWithoutMerchantName()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            HideSensitive = false
        });
        context.TransactionCategories.AddRange(
            new TransactionCategory { Id = "food", Name = "Food" },
            new TransactionCategory { Id = "social", Name = "Social" });
        context.Transactions.Add(Transaction(
            "exact-old-date",
            new DateTime(2023, 4, 5, 12, 0, 0, DateTimeKind.Utc),
            "Cafe",
            -20));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            new AiClient(
                new HttpClient(handler),
                TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache));

        var outcome = await service.ChatAsync(new AiChatRequest(
            "Edit the transaction on April 5, 2023 and change category to Social",
            []));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("exact-old-date", action.Payload["id"]);
        var changes = Assert.IsType<Dictionary<string, object?>>(action.Payload["changes"]);
        Assert.Equal("Social", changes["category"]);
        Assert.Equal(0, handler.CallCount);
    }

    private static Transaction Transaction(string id, DateTime date, string description, decimal amount) => new()
    {
        Id = id,
        Date = date,
        Description = description,
        Category = "Food",
        LedgerCategory = "Essentials",
        Amount = amount
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string UserContent { get; private set; } = string.Empty;
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(requestBody);
            UserContent = document.RootElement
                .GetProperty("contents")[0]
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? string.Empty;

            var modelText = "{\"reply\":\"Historical analysis ready.\",\"closeChat\":false,\"actions\":[]}";
            var providerBody = JsonSerializer.Serialize(new
            {
                candidates = new[]
                {
                    new
                    {
                        content = new { parts = new[] { new { text = modelText } } },
                        finishReason = "STOP"
                    }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(providerBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
