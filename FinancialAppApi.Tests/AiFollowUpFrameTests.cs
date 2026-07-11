using System.Net;
using System.Text;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

// Multi-turn follow-up behavior driven by the server-side resolved "frame" (AiConversationState).
// Each test runs a real sequence of turns, threading turn N's outcome.Response.State into turn
// N+1 exactly as the client does, and asserts on the prompt actually sent to the model
// (handler.UserContent) after the turn under test.
public class AiFollowUpFrameTests
{
    // Active cycle = Jul 2026 (cycleDay 1): "this cycle" = Jul, "last cycle" = Jun, "the one before
    // that" = May.
    private static AppDbContext SeededContext()
    {
        var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false, Currency = "USD" });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.AddRange(
            Tx("jul-huge", new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc), "Jul Huge", -250),
            Tx("jul-big", new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc), "Jul Big", -150),
            Tx("jul-mid", new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc), "Jul Mid", -120),
            Tx("jul-small", new DateTime(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc), "Jul Small", -50),
            Tx("jun-big", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), "Jun Big", -130),
            Tx("jun-small", new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc), "Jun Small", -40),
            Tx("may-big", new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc), "May Big", -110));
        context.SaveChanges();
        return context;
    }

    [Fact]
    public async Task ThresholdPersistsWhenCycleChanges_TheReportedBug()
    {
        await using var context = SeededContext();
        var (service, handler) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("Which transaction exceeded 100 this cycle?", []));
        Assert.Contains("thresholdMatches", handler.UserContent);
        Assert.Contains("Jul Big", handler.UserContent);
        Assert.Contains("\"month\":\"Jul\"", handler.UserContent);

        // Follow-up naming only the new cycle; the >100 threshold must carry over.
        var t2 = await service.ChatAsync(new AiChatRequest("how about last cycle", [], t1.Response.State));

        Assert.Contains("\"month\":\"Jun\"", handler.UserContent);
        Assert.Contains("thresholdMatches", handler.UserContent);
        Assert.Contains("\"comparator\":\"GreaterThan\"", handler.UserContent);
        Assert.Contains("\"low\":100", handler.UserContent);
        Assert.Contains("Jun Big", handler.UserContent);
        // Only the previous cycle is in scope now -- the active-cycle rows must not leak in.
        Assert.DoesNotContain("Jul Big", handler.UserContent);
        // ...and the prior turn's prose is never resent to the model.
        Assert.DoesNotContain("exceeded", handler.UserContent);
    }

    [Fact]
    public async Task RelativeReferenceThreeDeepStepsFromPriorResolvedCycle()
    {
        await using var context = SeededContext();
        var (service, handler) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("Which transaction exceeded 100 this cycle?", []));
        var t2 = await service.ChatAsync(new AiChatRequest("how about last cycle", [], t1.Response.State));
        var t3 = await service.ChatAsync(new AiChatRequest("and the one before that", [], t2.Response.State));

        // Jun -> the one before that = May, threshold still >100.
        Assert.Contains("\"month\":\"May\"", handler.UserContent);
        Assert.Contains("May Big", handler.UserContent);
        Assert.Contains("thresholdMatches", handler.UserContent);
        Assert.Contains("\"low\":100", handler.UserContent);
        Assert.DoesNotContain("Jun Big", handler.UserContent);
    }

    [Fact]
    public async Task ThresholdChangesWhileCyclePersists()
    {
        await using var context = SeededContext();
        var (service, handler) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("Which transaction exceeded 100 this cycle?", []));
        var t2 = await service.ChatAsync(new AiChatRequest("what about over 200", [], t1.Response.State));

        // Cycle stayed Jul; threshold moved to >200.
        Assert.Contains("\"month\":\"Jul\"", handler.UserContent);
        Assert.Contains("\"low\":200", handler.UserContent);
        Assert.Contains("Jul Huge", handler.UserContent);
    }

    [Fact]
    public async Task SearchTermPersistsWhenCycleChanges()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.Transactions.AddRange(
            Tx("sb-jul", new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc), "Starbucks", -8),
            Tx("sb-jun", new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc), "Starbucks", -9),
            Tx("noise-jun", new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc), "Grocery", -40));
        await context.SaveChangesAsync();
        var (service, handler) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("How much did I spend at Starbucks this cycle?", []));
        var t2 = await service.ChatAsync(new AiChatRequest("and last cycle?", [], t1.Response.State));

        Assert.Contains("\"searchText\":\"Starbucks\"", handler.UserContent);
        Assert.Contains("\"month\":\"Jun\"", handler.UserContent);
        Assert.DoesNotContain("Grocery", handler.UserContent);
    }

    [Fact]
    public async Task IntentSwitchToWishlistDropsThresholdAndCycle()
    {
        await using var context = SeededContext();
        context.WishlistItems.Add(new WishlistItem { Id = 42, Name = "Mechanical Keyboard", Price = 300, IsActive = true, IsPurchased = false });
        await context.SaveChangesAsync();
        var (service, handler) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("Which transaction exceeded 100 this cycle?", []));
        var t2 = await service.ChatAsync(new AiChatRequest("what's on my wishlist", [], t1.Response.State));

        // The wishlist turn must not inherit the >100 transaction filter.
        Assert.Contains("Mechanical Keyboard", handler.UserContent);
        Assert.DoesNotContain("thresholdMatches", handler.UserContent);
    }

    [Fact]
    public async Task DataFollowUpNeverResendsPriorProseEvenIfClientSuppliesHistory()
    {
        await using var context = SeededContext();
        var (service, handler) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("Which transaction exceeded 100 this cycle?", []));
        // Simulate an old/compat client still shipping the full history.
        var history = new[]
        {
            new AiChatMessage("user", "Which transaction exceeded 100 this cycle?"),
            new AiChatMessage("assistant", "There were 2 transactions exceeding 100 this cycle."),
        };
        await service.ChatAsync(new AiChatRequest("how about last cycle", history, t1.Response.State));

        // History is a reconstructed data query -> the prior exchange is stripped from the prompt.
        Assert.Contains("Recent chat JSON: []", handler.UserContent);
        Assert.DoesNotContain("There were 2 transactions", handler.UserContent);
    }

    [Fact]
    public async Task SemanticFollowUpReceivesBoundedPriorExchange()
    {
        await using var context = SeededContext();
        var (service, handler) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("How much did I spend this cycle?", []));
        var history = new[]
        {
            new AiChatMessage("user", "How much did I spend this cycle?"),
            new AiChatMessage("assistant", "You spent 570 this cycle."),
        };
        await service.ChatAsync(new AiChatRequest("is that good?", history, t1.Response.State));

        // A semantic follow-up about the assistant's own conclusion DOES get the prior exchange.
        Assert.DoesNotContain("Recent chat JSON: []", handler.UserContent);
        Assert.Contains("You spent 570 this cycle.", handler.UserContent);
    }

    [Fact]
    public async Task ReferentialFollowUpResolvesAgainstMatchedTransactionIds()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        context.Transactions.AddRange(
            Tx("bad-1", new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc), "Badminton court", -20),
            Tx("bad-2", new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc), "Badminton shuttlecock", -8));
        await context.SaveChangesAsync();
        var (service, handler) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("show badminton transactions this cycle", []));
        Assert.NotNull(t1.Response.State!.LastMatchedTransactionIds);
        var t2 = await service.ChatAsync(new AiChatRequest("which of those was the biggest?", [], t1.Response.State));

        Assert.Contains("referencedTransactions", handler.UserContent);
    }

    [Theory]
    [InlineData("the one before that", "2026-05")]
    [InlineData("the cycle before that", "2026-05")]
    [InlineData("what about the one after that", "2026-07")]
    public void RelativeCycleResolvesAgainstASingleAnchor(string message, string expectedKey)
    {
        var resolved = AiAssistantService.TryResolveRelativeToPriorCycle(message, ["2026-06"]);
        Assert.NotNull(resolved);
        Assert.Equal(expectedKey, $"{resolved!.Year:D4}-{resolved.MonthIndex:D2}");
    }

    [Fact]
    public void RelativeCycleDeclinesWhenPriorTurnResolvedMultipleCycles()
    {
        // "the one before that" has no unambiguous anchor after a comparison/range -> decline.
        Assert.Null(AiAssistantService.TryResolveRelativeToPriorCycle("the one before that", ["2026-07", "2026-06"]));
        Assert.Null(AiAssistantService.TryResolveRelativeToPriorCycle("the one before that", null));
    }

    [Fact]
    public async Task ExcludedCategoryIsStoredAndPersistsAcrossFollowUp()
    {
        await using var context = SeededContext();
        var (service, _) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("how much did I spend this cycle excluding Food", []));
        Assert.NotNull(t1.Response.State!.LastExcludedCategories);
        Assert.Contains(t1.Response.State!.LastExcludedCategories!, c => c.Contains("Food", StringComparison.OrdinalIgnoreCase));

        var t2 = await service.ChatAsync(new AiChatRequest("and last cycle?", [], t1.Response.State));
        // The exclusion sticks across the cycle change.
        Assert.NotNull(t2.Response.State!.LastExcludedCategories);
        Assert.Contains(t2.Response.State!.LastExcludedCategories!, c => c.Contains("Food", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IncludedCategoryIsStoredOnTheFrame()
    {
        await using var context = SeededContext();
        var (service, _) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("how much did I spend this cycle only Food", []));
        Assert.NotNull(t1.Response.State!.LastIncludedCategories);
        Assert.Contains(t1.Response.State!.LastIncludedCategories!, c => c.Contains("Food", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ComparisonScopeIsStoredOnTheFrame()
    {
        await using var context = SeededContext();
        var (service, _) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("compare this cycle vs last cycle", []));
        Assert.True(t1.Response.State!.LastComparison);
    }

    [Fact]
    public async Task TransactionTypeIsStoredOnTheFrame()
    {
        await using var context = SeededContext();
        var (service, _) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("show my outflow transactions this cycle", []));
        Assert.Equal("outflow", t1.Response.State!.LastTransactionType);
    }

    [Fact]
    public async Task NewClientFullStateIsSanitizedAndPreservedForForwardCompatibility()
    {
        await using var context = SeededContext();
        var (service, handler) = NewService(context);

        // A "new client" echoing back every frame field, including ones only a future turn sets.
        var state = new AiConversationState(
            LastIntent: "ledger.transaction_list",
            LastSearchText: null,
            LastCycleHint: null,
            LastWishlistReference: null,
            LastResolvedCycleKeys: ["2026-07"],
            LastAmountThreshold: new AiAmountThreshold("GreaterThan", 100m),
            LastExcludeTransfers: true,
            LastExcludedCategories: ["Food"],
            LastIncludedCategories: ["Transport"],
            LastLedgerCategory: "Growth",
            LastTransactionType: "outflow",
            LastExactDate: "2026-07-05",
            LastComparison: true,
            LastRecurringReference: "Netflix");

        var outcome = await service.ChatAsync(new AiChatRequest("what about over 200", [], state));

        // The threshold override applies and the sticky dimensions survive the round-trip.
        Assert.Contains("\"low\":200", handler.UserContent);
        Assert.Equal("Growth", outcome.Response.State!.LastLedgerCategory);
        Assert.Equal("2026-07-05", outcome.Response.State!.LastExactDate);
        Assert.True(outcome.Response.State!.LastExcludeTransfers);
    }

    [Fact]
    public async Task CycleFollowUpAfterWishlistReturnsToTransactionalFamily()
    {
        await using var context = SeededContext();
        context.WishlistItems.Add(new WishlistItem { Id = 5, Name = "Camera", Price = 300, IsActive = true, IsPurchased = false });
        await context.SaveChangesAsync();
        var (service, handler) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("Which transaction exceeded 100 this cycle?", []));
        var t2 = await service.ChatAsync(new AiChatRequest("what's on my wishlist", [], t1.Response.State));
        // A cycle follow-up right after a wishlist turn must resolve as transactional, not wishlist.
        var t3 = await service.ChatAsync(new AiChatRequest("how about last cycle", [], t2.Response.State));

        Assert.Contains("\"month\":\"Jun\"", handler.UserContent);
        Assert.Contains("Jun Big", handler.UserContent);
        Assert.DoesNotContain("Camera", handler.UserContent);
    }

    [Fact]
    public async Task ReferentialFollowUpWorksAfterThresholdQuery()
    {
        await using var context = SeededContext();
        var (service, handler) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("Which transaction exceeded 100 this cycle?", []));
        // Threshold queries now capture matched ids too, so "those" resolves.
        Assert.NotNull(t1.Response.State!.LastMatchedTransactionIds);
        Assert.NotEmpty(t1.Response.State!.LastMatchedTransactionIds!);
        var t2 = await service.ChatAsync(new AiChatRequest("which of those was the biggest?", [], t1.Response.State));

        Assert.Contains("referencedTransactions", handler.UserContent);
    }

    [Fact]
    public async Task ContextResetAbandonsTheCarriedFrame()
    {
        await using var context = SeededContext();
        var (service, handler) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("Which transaction exceeded 100 this cycle?", []));
        // "never mind" resets: the new question must not inherit the >100 threshold.
        var t2 = await service.ChatAsync(new AiChatRequest("never mind, how much did I spend this cycle?", [], t1.Response.State));

        Assert.DoesNotContain("thresholdMatches", handler.UserContent);
        Assert.Null(t2.Response.State!.LastAmountThreshold);
    }

    [Fact]
    public async Task IncludeTransfersFollowUpLiftsTheStickyExclusion()
    {
        await using var context = SeededContext();
        var (service, _) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("how much did I spend this cycle without transfers", []));
        Assert.True(t1.Response.State!.LastExcludeTransfers);
        var t2 = await service.ChatAsync(new AiChatRequest("actually include transfers", [], t1.Response.State));

        Assert.False(t2.Response.State!.LastExcludeTransfers);
    }

    [Fact]
    public async Task ClearFiltersFollowUpDropsStickyExclusions()
    {
        await using var context = SeededContext();
        var (service, _) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("how much did I spend this cycle excluding Food", []));
        Assert.NotNull(t1.Response.State!.LastExcludedCategories);
        var t2 = await service.ChatAsync(new AiChatRequest("show me everything this cycle", [], t1.Response.State));

        Assert.Null(t2.Response.State!.LastExcludedCategories);
    }

    [Fact]
    public async Task AnalysisTypeCarriesToNextCycle_UnusualSpendingFollowUp()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026, HideSensitive = false });
        // Both cycles: a cluster of small spends plus one clear outlier, so anomaly detection fires.
        context.Transactions.AddRange(
            Tx("j1", new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc), "Coffee", -10),
            Tx("j2", new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc), "Lunch", -12),
            Tx("j3", new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc), "Snack", -8),
            Tx("j4", new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc), "Bus", -9),
            Tx("j-out", new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc), "Jul Splurge", -200),
            Tx("m1", new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc), "Coffee", -11),
            Tx("m2", new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc), "Lunch", -13),
            Tx("m3", new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc), "Snack", -7),
            Tx("m4", new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc), "Bus", -10),
            Tx("m-out", new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc), "Jun Splurge", -300));
        await context.SaveChangesAsync();
        var (service, handler) = NewService(context);

        var t1 = await service.ChatAsync(new AiChatRequest("What unusual spending happened this cycle?", []));
        Assert.Contains("anomalies", handler.UserContent);

        // The follow-up only changes the cycle -> the anomaly analysis must re-run on June, not
        // collapse into a plain outflow total.
        var t2 = await service.ChatAsync(new AiChatRequest("how about previous cycle", [], t1.Response.State));

        Assert.Contains("anomalies", handler.UserContent);
        Assert.Contains("\"month\":\"Jun\"", handler.UserContent);
        Assert.Contains("Jun Splurge", handler.UserContent);
    }

    private static (AiAssistantService Service, CapturingHandler Handler) NewService(AppDbContext context)
    {
        var handler = new CapturingHandler();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiAssistantService(
            new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")), NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache));
        return (service, handler);
    }

    private static Transaction Tx(string id, DateTime date, string description, decimal amount) => new()
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
            UserContent = document.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString() ?? string.Empty;

            var modelText = "{\"reply\":\"ok\",\"closeChat\":false,\"actions\":[]}";
            var providerBody = JsonSerializer.Serialize(new
            {
                candidates = new[] { new { content = new { parts = new[] { new { text = modelText } } }, finishReason = "STOP" } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(providerBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
