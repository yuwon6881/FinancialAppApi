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
            new Transaction
            {
                Id = "old-salary", Date = new DateTime(2023, 3, 2, 12, 0, 0, DateTimeKind.Utc),
                Description = "Old Cycle Salary", Category = "Salary", LedgerCategory = "Income", Amount = 100
            },
            new Transaction
            {
                Id = "old-reimbursement", Date = new DateTime(2023, 3, 3, 12, 0, 0, DateTimeKind.Utc),
                Description = "Old Cycle Reimbursement", Category = "Reimbursement", LedgerCategory = "Stability", Amount = 25
            },
            Transaction("new", new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc), "Newest Lunch", -99));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var aiClient = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
            NullLogger<AiClient>.Instance);
        var service = new AiAssistantService(aiClient, context, new TransactionCategoryService(context, cache));

        var outcome = await service.ChatAsync(new AiChatRequest("How much did I spend in the March 2023 cycle?", []));

        Assert.False(outcome.IsProviderError);
        Assert.Contains("Old Cycle Coffee", handler.UserContent);
        Assert.DoesNotContain("Newest Lunch", handler.UserContent);
        Assert.Contains("\"month\":\"Mar\"", handler.UserContent);
        Assert.Contains("\"income\":100", handler.UserContent);
        Assert.Contains("\"inflow\":125", handler.UserContent);
        Assert.Contains("\"otherInflow\":25", handler.UserContent);
        Assert.Contains("\"outflow\":42", handler.UserContent);
        Assert.Contains("\"aggregatesCoverAllTransactionsInRequestedCycles\":true", handler.UserContent);
        Assert.DoesNotContain("\"categoryLimits\"", handler.UserContent);
        Assert.DoesNotContain("\"cycleInsights\"", handler.UserContent);
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
            TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
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
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
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
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
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
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
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
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
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
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
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
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("How can I improve my spending this cycle?", []));

        // budgetTargets are amount-oriented guidance the app withholds in sensitive mode: the
        // data block and its allocation values must not reach the prompt. (The sufficiency gate
        // may still *name* budgetTargets as a hidden/missing dataset -- that leaks no values.)
        Assert.DoesNotContain("Fractions of income", handler.UserContent);
        Assert.DoesNotContain("\"essentials\":0.5", handler.UserContent);
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
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
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
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
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

    [Fact]
    public async Task ChatAsync_CountActivityLoadsCycleTransactions()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.TransactionCategories.Add(new TransactionCategory { Id = "sport", Name = "Sports" });
        context.Transactions.Add(Transaction("badminton", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), "Badminton court", -12));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("How many badminton I played last cycle?", []));

        Assert.Contains("Badminton court", handler.UserContent);
        Assert.Contains("\"detailedTransactionsIncluded\":1", handler.UserContent);
        Assert.Contains("\"month\":\"Jun\"", handler.UserContent);
        Assert.Contains("transactionMatches", handler.UserContent);
        Assert.Contains("\"count\":1", handler.UserContent);
        Assert.Contains("\"searchText\":\"badminton\"", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_CountRelatedTransactions_StripsRequestScaffoldingFromSearch()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 28, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.Transactions.AddRange(
            // Cycle day 28 puts the previous cycle at Jun 28 ~ Jul 27, so "last cycle" dates sit in
            // July here -- a June date would fall in the cycle before it and match nothing.
            Transaction("badminton-one", new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc), "Badminton", -10),
            Transaction("badminton-two", new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc), "Badminton Shuttlecock", -113));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("how many badminton related transaction i spent last cycle?", []));

        Assert.Contains("\"searchText\":\"badminton\"", handler.UserContent);
        Assert.Contains("\"count\":2", handler.UserContent);
        Assert.DoesNotContain("\"searchText\":\"badminton related transaction\"", handler.UserContent);
    }

    [Theory]
    [InlineData("last cycle has how many badminton records")]
    [InlineData("how many badminton-related transactions were there last cycle?")]
    [InlineData("number of badminton payments last cycle")]
    [InlineData("how many records for badminton in the last cycle?")]
    public async Task ChatAsync_CountTransactionWordingVariants_ResolveTheSameSubject(string message)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 28, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        // Cycle day 28 puts the previous cycle at Jun 28 ~ Jul 27, so this sits inside "last cycle".
        context.Transactions.Add(Transaction("badminton", new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc), "Badminton Shuttlecock", -113));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest(message, []));

        Assert.Contains("\"searchText\":\"badminton\"", handler.UserContent);
        Assert.Contains("\"count\":1", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_WishlistForecastLoadsServerEstimate()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.WishlistItems.Add(new WishlistItem { Id = 7, Name = "Badminton racket", Price = 100, IsActive = true, IsPurchased = false });
        // Savings toward a wishlist goal are positive Rewards-ledger attribution (the forecaster
        // mirrors the app's past-Rewards-average, not total net cash flow).
        context.Transactions.AddRange(
            RewardsSaving("saving-apr", new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc), 200),
            RewardsSaving("saving-may", new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc), 200),
            RewardsSaving("saving-jun", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), 200));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("How long can I hit my wishlist target?", []));

        Assert.Contains("wishlistForecast", handler.UserContent);
        Assert.Contains("Badminton racket", handler.UserContent);
        Assert.Contains("estimatedCycles", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_SensitiveWishlistForecastIsBlockedBeforeModelCall()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = true });
        context.WishlistItems.Add(new WishlistItem { Id = 8, Name = "Camera", Price = 500, IsActive = true, IsPurchased = false });
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        var outcome = await service.ChatAsync(new AiChatRequest("How long until I hit my wishlist target?", []));

        Assert.Equal(0, handler.CallCount);
        Assert.Contains("sensitive mode", outcome.Response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChatAsync_FollowUpCarriesStructuredConversationReferences()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.Transactions.Add(Transaction("badminton", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), "Badminton court", -12));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        // Two real turns: the follow-up now carries context through the structured frame
        // (outcome.Response.State), not through resent prior-message prose.
        var first = await service.ChatAsync(new AiChatRequest("How many badminton I played last cycle?", []));
        await service.ChatAsync(new AiChatRequest("What about those?", [], first.Response.State));

        Assert.Contains("conversationState", handler.UserContent);
        Assert.Contains("badminton", handler.UserContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChatAsync_MerchantSearchLoadsOnlyMatchingRowsAndMetric()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.Transactions.AddRange(
            Transaction("coffee", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Starbucks", -8),
            Transaction("other", new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc), "Grocery store", -40));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("Show Starbucks spending", []));

        Assert.Contains("Starbucks", handler.UserContent);
        Assert.DoesNotContain("Grocery store", handler.UserContent);
        Assert.Contains("\"searchText\":\"Starbucks\"", handler.UserContent);
        Assert.Contains("\"count\":1", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_UnknownNaturalLanguageStillProducesStructuredIntentContext()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.Add(Transaction("coffee", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Coffee shop", -8));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("Where did my money go this month?", []));

        Assert.Contains("\"intents\"", handler.UserContent);
        Assert.Contains("\"sufficiency\"", handler.UserContent);
        Assert.Contains("cycleSummaries", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_AllCyclesScopeSearchesFullHistoryNotJustActiveCycle()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.TransactionCategories.Add(new TransactionCategory { Id = "sport", Name = "Sports" });
        context.Transactions.AddRange(
            Transaction("bad-old", new DateTime(2025, 1, 12, 12, 0, 0, DateTimeKind.Utc), "Badminton Old", -20),
            Transaction("bad-now", new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc), "Badminton Court", -20));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("show badminton transactions for all cycles", []));

        // Both the historical and the current-cycle record are found, and the exact match count
        // spans the whole history -- not just the active cycle (the old bug returned only current).
        Assert.Contains("Badminton Old", handler.UserContent);
        Assert.Contains("Badminton Court", handler.UserContent);
        Assert.Contains("\"searchText\":\"badminton\"", handler.UserContent);
        Assert.Contains("\"count\":2", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_PurchaseFrequencySearchesAllHistoryAndBuildsAuthoritativeCadence()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false
        });
        var transfer = Transaction("transfer", new DateTime(2023, 3, 14, 12, 0, 0, DateTimeKind.Utc), "Deodorant transfer", -10);
        transfer.Category = "Transfer";
        transfer.LedgerCategory = "Transfer:Essentials->Rewards";
        var adjustment = Transaction("adjustment", new DateTime(2023, 3, 15, 12, 0, 0, DateTimeKind.Utc), "Deodorant adjustment", -10);
        adjustment.Category = "Adjustment";
        var discarded = Transaction("discarded", new DateTime(2023, 3, 16, 12, 0, 0, DateTimeKind.Utc), "Deodorant discarded", -10);
        discarded.LedgerCategory = "Discarded";
        var structural = Transaction("structural", new DateTime(2023, 3, 17, 12, 0, 0, DateTimeKind.Utc), "Deodorant system row", -10);
        structural.ExcludeFromAutocomplete = true;
        context.Transactions.AddRange(
            Transaction("one", new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc), "Watsons Deodorant", -12),
            Transaction("same-day", new DateTime(2023, 1, 1, 18, 0, 0, DateTimeKind.Utc), "Deodorant refill", -5),
            Transaction("two", new DateTime(2023, 1, 31, 12, 0, 0, DateTimeKind.Utc), "Deodorant", -11),
            Transaction("three", new DateTime(2023, 3, 12, 12, 0, 0, DateTimeKind.Utc), "Deodorant spray", -10),
            Transaction("refund", new DateTime(2023, 3, 13, 12, 0, 0, DateTimeKind.Utc), "Deodorant refund", 10),
            transfer,
            adjustment,
            discarded,
            structural);
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            NewClient(handler), context, new TransactionCategoryService(context, cache),
            financialClock: TestClock(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)));

        await service.ChatAsync(new AiChatRequest(
            "Based on historical data, approximately whats the frequency do i buy deodorant?", []));

        Assert.Contains("\"intents\":[\"ledger.purchase_frequency\"", handler.UserContent);
        Assert.Contains("\"searchText\":\"deodorant\"", handler.UserContent);
        Assert.Contains("\"allHistory\":true", handler.UserContent);
        Assert.Contains("\"matchMode\":\"exact\"", handler.UserContent);
        Assert.Contains("\"transactionCount\":4", handler.UserContent);
        Assert.Contains("\"purchaseDayCount\":3", handler.UserContent);
        Assert.Contains("\"medianGapDays\":35", handler.UserContent);
        Assert.Contains("\"typicalCadence\":\"about every 5 weeks\"", handler.UserContent);
        Assert.Contains("\"firstPurchaseDate\":\"2023-01-01\"", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_PurchaseFrequencyUsesFuzzyOnlyAfterExactReturnsNothing()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false
        });
        context.Transactions.Add(Transaction(
            "correct", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), "Watsons Deodorant", -12));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            NewClient(handler), context, new TransactionCategoryService(context, cache),
            financialClock: TestClock(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)));

        await service.ChatAsync(new AiChatRequest("How often do I buy deoderant?", []));

        Assert.Contains("\"matchMode\":\"fuzzy\"", handler.UserContent);
        Assert.Contains("\"transactionCount\":1", handler.UserContent);
        Assert.Contains("Watsons Deodorant", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_PurchaseFrequencyExactMatchWinsOverFuzzyCandidates()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false
        });
        context.Transactions.AddRange(
            Transaction("typed", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), "Deoderant typed exactly", -12),
            Transaction("similar", new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), "Deodorant correct spelling", -12));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            NewClient(handler), context, new TransactionCategoryService(context, cache),
            financialClock: TestClock(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)));

        await service.ChatAsync(new AiChatRequest("How frequently do I buy deoderant?", []));

        Assert.Contains("\"matchMode\":\"exact\"", handler.UserContent);
        Assert.Contains("\"transactionCount\":1", handler.UserContent);
        Assert.Contains("Deoderant typed exactly", handler.UserContent);
        Assert.DoesNotContain("Deodorant correct spelling", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_PurchaseFrequencyFollowUpRetainsAllHistoryScope()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false
        });
        context.Transactions.AddRange(
            Transaction("deodorant", new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc), "Deodorant", -12),
            Transaction("shampoo", new DateTime(2023, 2, 1, 12, 0, 0, DateTimeKind.Utc), "Shampoo", -8));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            NewClient(handler), context, new TransactionCategoryService(context, cache),
            financialClock: TestClock(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)));

        var first = await service.ChatAsync(new AiChatRequest("How often do I buy deodorant?", []));
        await service.ChatAsync(new AiChatRequest("What about shampoo?", [], first.Response.State));

        Assert.Equal("all history", first.Response.State?.LastCycleHint);
        Assert.Contains("\"allHistory\":true", handler.UserContent);
        Assert.Contains("\"query\":\"shampoo\"", handler.UserContent);
        Assert.Contains("Shampoo", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_ExistenceAcrossLastThreeCyclesCountsRecordDatedInLaterCalendarMonth()
    {
        // CycleDay 28: the "May" cycle runs May 28 -> Jun 27, so a record dated Jun 24 belongs to
        // it. Active cycle is Jun (Jun 28 -> Jul 27); "last 3 cycles" therefore includes the May
        // cycle, and the Jun-dated record must be counted despite its calendar month.
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 28, SelectedMonth = "Jun", SelectedYear = 2026, HideSensitive = false });
        context.TransactionCategories.Add(new TransactionCategory { Id = "sport", Name = "Sports" });
        context.Transactions.Add(Transaction("bad-may-cycle", new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc), "Badminton", -20));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("any badminton transactions in last 3 cycles?", []));

        Assert.Contains("\"searchText\":\"badminton\"", handler.UserContent);
        Assert.Contains("\"count\":1", handler.UserContent);
        Assert.Contains("\"month\":\"May\"", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_LedgerNetIncludesTransfersIntoStability()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.Transactions.Add(new Transaction
        {
            Id = "xfer",
            Date = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc),
            Description = "Move to Stability",
            Category = "Transfer",
            LedgerCategory = "Transfer:Essentials->Stability",
            Amount = 100
        });
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("deeply analyse this cycle", []));

        // The transfer feeds Stability (+100) and drains Essentials (-100); the old bug summed
        // ledgerNet over non-transfer rows only, so Stability wrongly showed 0.
        Assert.Contains("\"ledgerCategory\":\"Stability\",\"net\":100", handler.UserContent);
        Assert.Contains("\"ledgerCategory\":\"Essentials\",\"net\":-100", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_DiscardedBillSurfacesBillStatusMetric()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "netflix",
            Name = "Netflix",
            Amount = 15,
            Category = "Entertainment",
            LedgerCategory = "Rewards",
            Frequency = "Monthly",
            StartDate = "2026-01-01",
            DueDate = 15,
            Active = true
        });
        // The bill's charge this cycle was discarded: a zero-amount row tagged with the
        // occurrence it settles, which is what discarding actually writes.
        context.Transactions.Add(new Transaction
        {
            Id = "netflix-jul",
            Date = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
            Description = "Netflix",
            Category = "Entertainment",
            LedgerCategory = "Discarded",
            Amount = 0,
            RecurringPaymentId = "netflix",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 15)
        });
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("which subscription was discarded this cycle?", []));

        Assert.Contains("recurringBillStatus", handler.UserContent);
        Assert.Contains("\"name\":\"Netflix\"", handler.UserContent);
        Assert.Contains("\"status\":\"Discarded\"", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_GoalWordingTriggersWishlistForecast()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.WishlistItems.Add(new WishlistItem { Id = 9, Name = "Thai Trip", Price = 1000, IsActive = true, IsPurchased = false });
        context.Transactions.AddRange(
            RewardsSaving("r-apr", new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc), 200),
            RewardsSaving("r-may", new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc), 200));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        // "goal" + "achieve" now routes to the forecast intent (previously matched neither signal).
        await service.ChatAsync(new AiChatRequest("is my active goal hard to achieve?", []));

        Assert.Contains("wishlistForecast", handler.UserContent);
        Assert.Contains("Thai Trip", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_EditMultiMatchClarificationCarriesCandidateStateForFollowUp()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.TransactionCategories.Add(new TransactionCategory { Id = "sport", Name = "Sports" });
        context.Transactions.AddRange(
            Transaction("bad-1", new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc), "Badminton", -20),
            Transaction("bad-2", new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc), "Badminton Shuttlecock", -8));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        var outcome = await service.ChatAsync(new AiChatRequest("edit badminton in the previous cycle", []));

        // Deterministic clarification (no model call) that now carries the candidate ids so a
        // follow-up like "the one named Badminton" can be resolved against them.
        Assert.Equal(0, handler.CallCount);
        Assert.Contains("multiple matches", outcome.Response.Reply);
        Assert.NotNull(outcome.Response.State);
        Assert.NotNull(outcome.Response.State!.LastMatchedTransactionIds);
        Assert.Equal(2, outcome.Response.State!.LastMatchedTransactionIds!.Count);
    }

    [Fact]
    public async Task ChatAsync_UpcomingBillsMetricAndFrequencySurfaced()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.RecurringPayments.AddRange(
            new RecurringPayment { Id = "spotify", Name = "Spotify", Amount = 15, Frequency = "Monthly", Category = "Entertainment", LedgerCategory = "Rewards", NextDueDate = "2026-07-20", DueDate = 20, StartDate = "2026-01-01", Active = true },
            new RecurringPayment { Id = "domain", Name = "Domain Renewal", Amount = 40, Frequency = "Annually", Category = "Software", LedgerCategory = "Essentials", NextDueDate = "2026-11-01", DueDate = 1, StartDate = "2026-01-01", Active = true });
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("when is my next bill due?", []));

        Assert.Contains("upcomingBills", handler.UserContent);
        Assert.Contains("\"nextDueDate\":\"2026-07-20\"", handler.UserContent);
        Assert.Contains("\"frequency\":\"Monthly\"", handler.UserContent);
        // Soonest first: Spotify (Jul 20) before Domain (Nov 1).
        Assert.True(handler.UserContent.IndexOf("Spotify", StringComparison.Ordinal) < handler.UserContent.IndexOf("Domain Renewal", StringComparison.Ordinal));
    }

    // Status follows the occurrence a transaction *tags*, not the cycle it posted in. Both bills
    // here were paid on the same July day; the one whose payment settles August's occurrence is
    // still pending for July, and the one settling July's is paid.
    [Fact]
    public async Task ChatAsync_RecurringStatusFollowsTheOccurrenceTheTransactionTags()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.RecurringPayments.AddRange(
            new RecurringPayment
            {
                Id = "chatgpt-plus",
                Name = "ChatGPT Plus",
                Amount = 15,
                Frequency = "Monthly",
                Category = "Entertainment",
                LedgerCategory = "Rewards",
                NextDueDate = "2026-07-27",
                DueDate = 27,
                StartDate = "2026-01-27",
                Active = true
            },
            new RecurringPayment
            {
                Id = "music-pass",
                Name = "Music Pass",
                Amount = 20,
                Frequency = "Monthly",
                Category = "Entertainment",
                LedgerCategory = "Rewards",
                NextDueDate = "2026-07-21",
                DueDate = 21,
                StartDate = "2026-01-21",
                Active = true
            });
        context.Transactions.AddRange(
            new Transaction
            {
                Id = "chatgpt-next-occurrence",
                Date = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc),
                Description = "ChatGPT Plus",
                Category = "Entertainment",
                LedgerCategory = "Rewards",
                Amount = -15,
                RecurringPaymentId = "chatgpt-plus",
                RecurringOccurrenceDate = new DateOnly(2026, 8, 27)
            },
            new Transaction
            {
                Id = "music-pass-payment",
                Date = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc),
                Description = "Music Pass",
                Category = "Entertainment",
                LedgerCategory = "Rewards",
                Amount = -20,
                RecurringPaymentId = "music-pass",
                RecurringOccurrenceDate = new DateOnly(2026, 7, 21)
            });
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("which recurring bills are pending this cycle?", []));

        Assert.Contains("ChatGPT Plus", handler.UserContent);
        Assert.Contains("Music Pass", handler.UserContent);
        Assert.Contains("\"status\":\"Pending\"", handler.UserContent);
        Assert.Contains("\"status\":\"Paid\"", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_StabilityFundProgressMetricSurfaced()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false, TargetStabilityFund = 1000 });
        context.Transactions.Add(new Transaction
        {
            Id = "to-stability",
            Date = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc),
            Description = "Top up",
            Category = "Transfer",
            LedgerCategory = "Transfer:Essentials->Stability",
            Amount = 300
        });
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("how close am I to my stability fund goal?", []));

        Assert.Contains("stabilityProgress", handler.UserContent);
        Assert.Contains("\"currentStabilityBalance\":300", handler.UserContent);
        Assert.Contains("\"targetStabilityFund\":1000", handler.UserContent);
        Assert.Contains("\"percentReached\":30", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_AffordableWishlistCountSurfaced()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.WishlistItems.AddRange(
            new WishlistItem { Id = 1, Name = "Cheap Mouse", Price = 50, IsActive = true, IsPurchased = false },
            new WishlistItem { Id = 2, Name = "Expensive Trip", Price = 500, IsActive = false, IsPurchased = false });
        // Rewards balance this cycle = 200, covers only the 50 item.
        context.Transactions.Add(RewardsSaving("r", new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc), 200));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("how many wishlist items can I afford right now?", []));

        Assert.Contains("\"affordableWishlistCount\":1", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_WishlistPurchasedAtSurfaced()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.WishlistItems.Add(new WishlistItem { Id = 3, Name = "Headphones", Price = 200, IsActive = false, IsPurchased = true, PurchasedAt = new DateTime(2026, 5, 4, 9, 0, 0, DateTimeKind.Utc) });
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("when did I buy the headphones from my wishlist?", []));

        Assert.Contains("Headphones", handler.UserContent);
        Assert.Contains("purchasedAt", handler.UserContent);
        Assert.Contains("2026-05-04", handler.UserContent);
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

    private static Transaction RewardsSaving(string id, DateTime date, decimal amount) => new()
    {
        Id = id,
        Date = date,
        Description = "Rewards saving",
        Category = "Salary",
        LedgerCategory = "Rewards",
        Amount = amount
    };

    [Fact]
    public async Task ChatAsync_GenericTransactionPhraseUsesPartialMatchInRequestedCycles()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.Transactions.AddRange(
            Transaction("tng", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), "TNG eWallet Reload", -30),
            Transaction("other", new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc), "Groceries", -50));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("Any TNG transactions in last 3 cycles?", []));

        Assert.Contains("TNG eWallet Reload", handler.UserContent);
        Assert.DoesNotContain("Groceries", handler.UserContent);
        Assert.Contains("\"searchText\":\"TNG\"", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_SpendingOnDescriptionIncludesAuthoritativeMatchedTotal()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.Transactions.AddRange(
            Transaction("one", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), "Badminton court", -20),
            Transaction("two", new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc), "Post badminton meal", -12));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("How much I spend on badminton last cycle", []));

        Assert.Contains("\"searchText\":\"badminton\"", handler.UserContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"totalOutflow\":32", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_DailyExtremesAreComputedByServer()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.Transactions.AddRange(
            Transaction("income", new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc), "Salary", 1000),
            Transaction("small", new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc), "Lunch", -20),
            Transaction("large", new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc), "Rent", -500));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance), context, new TransactionCategoryService(context, cache));

        await service.ChatAsync(new AiChatRequest("Which day I received the most vs spent the most this cycle?", []));

        Assert.Contains("dailyExtremes", handler.UserContent);
        Assert.Contains("\"date\":\"2026-07-02\",\"inflow\":1000", handler.UserContent);
        Assert.Contains("\"date\":\"2026-07-04\",\"inflow\":0,\"outflow\":500", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_CategoryLimitQuestionIncludesEffectiveProgress()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.CategorySpendingGuides.Add(new CategorySpendingGuide
        {
            Id = "food-limit", CategoryName = "Food", EffectiveFromCycleKey = "2026-07", LimitAmount = 100
        });
        context.Transactions.Add(Transaction(
            "lunch", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Lunch", -75));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = TestClock(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));
        var service = new AiAssistantService(
            NewClient(handler), context, new TransactionCategoryService(context, cache), financialClock: clock);

        await service.ChatAsync(new AiChatRequest("Food budget?", []));

        Assert.Contains("\"categoryLimits\"", handler.UserContent);
        Assert.Contains("\"category\":\"Food\"", handler.UserContent);
        Assert.Contains("\"limit\":100", handler.UserContent);
        Assert.Contains("\"spent\":75", handler.UserContent);
        Assert.Contains("\"isComplete\":false", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_CurrentCycleSummaryKeepsBaseContextAndMarksInsightsInProgress()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.Add(Transaction(
            "dinner", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Dinner", -60));
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            NewClient(handler), context, new TransactionCategoryService(context, cache),
            financialClock: TestClock(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero)));

        await service.ChatAsync(new AiChatRequest("How am I tracking?", []));

        Assert.Contains("\"cycleSummaries\"", handler.UserContent);
        Assert.Contains("\"cycleInsights\"", handler.UserContent);
        Assert.Contains("\"phase\":\"InProgress\"", handler.UserContent);
        Assert.Contains("\"observedThrough\":\"2026-07-15\"", handler.UserContent);
        Assert.Contains("\"remainingDays\":16", handler.UserContent);
        Assert.Contains("\"averageDailySpend\":4", handler.UserContent);
    }

    [Fact]
    public async Task ChatAsync_RecurringReminderAndPayEarlyQuestionIncludesCapabilities()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "software", Name = "Software" });
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "spotify", Name = "Spotify", Amount = 15, Frequency = "Monthly",
            Category = "Software", LedgerCategory = "Rewards", NextDueDate = "2026-07-20",
            DueDate = 20, StartDate = "2026-01-20", Active = true,
            PushReminderEnabled = true, PushReminderMode = "Daily", PushReminderLeadDays = 3
        });
        // A reminder is only "effective" if some device can actually receive it. Enabled
        // subscription rows are the whole account-level opt-in -- there is no flag beside them.
        context.PushSubscriptions.Add(new PushSubscription
        {
            Id = "push-1",
            DeviceId = "device-1",
            FcmToken = "token-1",
            Enabled = true
        });
        await context.SaveChangesAsync();

        var handler = new CapturingHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            NewClient(handler), context, new TransactionCategoryService(context, cache),
            financialClock: TestClock(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero)));

        await service.ChatAsync(new AiChatRequest(
            "Can I pay Spotify early and are its subscription push reminders enabled?", []));

        Assert.Contains("\"recurringAdvance\"", handler.UserContent);
        Assert.Contains("\"canPayEarly\":true", handler.UserContent);
        Assert.Contains("\"nextUnpaidOccurrence\":\"2026-07-20\"", handler.UserContent);
        Assert.Contains("\"recurringReminderStatus\"", handler.UserContent);
        Assert.Contains("\"effective\":true", handler.UserContent);
        Assert.Contains("\"mode\":\"Daily\"", handler.UserContent);
        Assert.Contains("\"leadDays\":3", handler.UserContent);
    }

    private static AiClient NewClient(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
        NullLogger<AiClient>.Instance);

    private static FinancialClock TestClock(DateTimeOffset now) => new(
        TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
        new FixedTimeProvider(now));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

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
                .GetProperty("input")[0]
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString() ?? string.Empty;

            var modelText = "{\"reply\":\"Historical analysis ready.\",\"closeChat\":false,\"actions\":[]}";
            var providerBody = JsonSerializer.Serialize(new
            {
                status = "completed",
                output = new[]
                {
                    new
                    {
                        type = "message",
                        content = new[] { new { type = "output_text", text = modelText } }
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

