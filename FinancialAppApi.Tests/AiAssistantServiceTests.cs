using System.Net;
using System.Text;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

// Tests for the hybrid AI assistant pipeline: deterministic guardrails, intent routing +
// classifier fallback, context shaping, deterministic ledger-edit resolution, and returned
// action validation. The seam is ScriptedAiHandler, which captures the exact prompt sent to
// the model (so we assert on what the *server decided to send*, not on model phrasing) and
// returns queued replies so multi-call flows (classifier -> chat) can be driven precisely.
public class AiAssistantServiceTests
{
    // ---------- Layer: deterministic guardrails (zero model calls) ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ChatAsync_BlankMessage_AsksForAQuestionWithoutCallingModel(string message)
    {
        await using var context = NewContextWithSettings();
        var handler = new ScriptedAiHandler();
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest(message, []));

        Assert.False(outcome.IsProviderError);
        Assert.Equal(0, handler.CallCount);
        Assert.Contains("financial question", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_OverlongMessage_IsRejectedWithoutCallingModel()
    {
        await using var context = NewContextWithSettings();
        var handler = new ScriptedAiHandler();
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest(new string('a', 2001), []));

        Assert.Equal(0, handler.CallCount);
        Assert.Contains("too long", outcome.Response.Reply);
    }

    [Theory]
    [InlineData("delete my coffee transaction")]
    [InlineData("please remove that record")]
    [InlineData("can you erase this entry")]
    public async Task ChatAsync_DeleteCommand_RefusesWithoutCallingModel(string message)
    {
        await using var context = NewContextWithSettings();
        var handler = new ScriptedAiHandler();
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest(message, []));

        Assert.Equal(0, handler.CallCount);
        Assert.Contains("unable to delete", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_QuestionContainingDeleteWord_IsNotTreatedAsDeleteCommand()
    {
        await using var context = NewContextWithSettings();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Here is some advice."));
        var service = NewService(context, handler);

        // Starts with "what " -> the delete guard deliberately lets it through to the model.
        var outcome = await service.ChatAsync(new AiChatRequest("What should I delete to save money?", []));

        Assert.True(handler.CallCount >= 1);
        Assert.DoesNotContain("unable to delete", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_Greeting_RepliesWithoutCallingModel()
    {
        await using var context = NewContextWithSettings();
        var handler = new ScriptedAiHandler();
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("hello!", []));

        Assert.Equal(0, handler.CallCount);
        Assert.False(outcome.Response.CloseChat);
        Assert.Contains("Ask me a financial question", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_Farewell_ClosesChatWithoutCallingModel()
    {
        await using var context = NewContextWithSettings();
        var handler = new ScriptedAiHandler();
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("bye", []));

        Assert.Equal(0, handler.CallCount);
        Assert.True(outcome.Response.CloseChat);
    }

    [Fact]
    public async Task ChatAsync_Acknowledgment_RepliesWithoutCallingModel()
    {
        await using var context = NewContextWithSettings();
        var handler = new ScriptedAiHandler();
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("thanks", []));

        Assert.Equal(0, handler.CallCount);
        Assert.Contains("Anytime", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_SentenceStartingWithAckWord_StillReachesModel()
    {
        await using var context = NewContextWithSettings();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Sure."));
        var service = NewService(context, handler);

        // Small-talk detection is an exact-match closed list, not Contains: a real question
        // that merely starts with "thanks" must not be swallowed as an acknowledgment.
        var outcome = await service.ChatAsync(new AiChatRequest("thanks, now how much did I spend this month?", []));

        Assert.True(handler.CallCount >= 1);
    }

    [Fact]
    public async Task ChatAsync_NotConfigured_ReportsUnconfiguredWithoutCallingModel()
    {
        await using var context = NewContextWithSettings();
        // No AiApiKey -> IsConfigured is false.
        var handler = new ScriptedAiHandler();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("AiModel", "test-model")),
            NullLogger<AiClient>.Instance);
        var service = new AiAssistantService(client, context, new TransactionCategoryService(context, cache));

        var outcome = await service.ChatAsync(new AiChatRequest("How much did I spend this month?", []));

        Assert.Equal(0, handler.CallCount);
        Assert.Contains("not configured", outcome.Response.Reply);
    }

    // ---------- Layer: intent routing & classifier fallback ----------

    [Fact]
    public async Task ChatAsync_ConfidentDeterministicIntent_DoesNotInvokeClassifier()
    {
        await using var context = NewContextWithSettings();
        context.Transactions.Add(Txn("t", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("You spent 80."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("How much did I spend this month?", []));

        // A single, clear intent routes straight to the answer model: exactly one call, and it
        // is the chat call (never the classifier).
        Assert.Equal(1, handler.CallCount);
        Assert.False(handler.WasClassifierCall(0));
    }

    [Fact]
    public async Task ChatAsync_AmbiguousMessage_FallsBackToClassifierThenAnswers()
    {
        await using var context = NewContextWithSettings();
        context.Transactions.Add(Txn("t", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Classifier(["ledger.spending_total"], 0.8, cycleHint: "this month"),
            ScriptedAiHandler.Chat("You spent 80 this month."));
        var service = NewService(context, handler);

        // No domain keywords -> deterministic plan is "general" (low confidence) -> classifier.
        await service.ChatAsync(new AiChatRequest("tell me something interesting", []));

        Assert.Equal(2, handler.CallCount);
        Assert.True(handler.WasClassifierCall(0));
        Assert.False(handler.WasClassifierCall(1));
        // The merged classifier intent pulls the cycle summary into the answer prompt.
        Assert.Contains("cycleSummaries", handler.UserContents[1]);
    }

    [Fact]
    public async Task ChatAsync_ClassifierFailure_StillAnswersFromDeterministicPlan()
    {
        await using var context = NewContextWithSettings();
        await context.SaveChangesAsync();
        // First (classifier) reply is unparseable JSON; the service must swallow it and proceed.
        var handler = new ScriptedAiHandler(
            "not-json",
            ScriptedAiHandler.Chat("Here is a general reply."));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("tell me something interesting", []));

        Assert.False(outcome.IsProviderError);
        Assert.Contains("general reply", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_ClassifierValidJsonButNoUsableIntents_FallsBackToDeterministicPlan()
    {
        await using var context = NewContextWithSettings();
        await context.SaveChangesAsync();
        // Valid JSON, but empty/unknown intents -> SanitizeClassification returns null.
        var handler = new ScriptedAiHandler(
            "{\"intents\":[],\"confidence\":0.9}",
            ScriptedAiHandler.Chat("General reply."));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("tell me something interesting", []));

        Assert.False(outcome.IsProviderError);
        Assert.Contains("General reply", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_ClassifierUnknownIntent_IsIgnored()
    {
        await using var context = NewContextWithSettings();
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(
            "{\"intents\":[\"malicious.intent\"],\"confidence\":0.99}",
            ScriptedAiHandler.Chat("Safe reply."));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("tell me something interesting", []));

        Assert.Contains("Safe reply", outcome.Response.Reply);
    }

    // ---------- Layer: context shaping & sensitive mode ----------

    [Fact]
    public async Task ChatAsync_RecurringQuestion_IncludesRecurringPaymentsInPrompt()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.RecurringPayments.Add(new RecurringPayment
        {
            Name = "Netflix",
            Amount = 15m,
            Category = "Entertainment",
            LedgerCategory = "Rewards",
            Active = true
        });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Here are your subscriptions."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("show my subscriptions", []));

        Assert.Contains("Netflix", handler.LastUserContent);
        Assert.Contains("\"amount\":15", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_SensitiveMode_OmitsRecurringAmounts()
    {
        await using var context = NewContextWithSettings(hideSensitive: true);
        context.RecurringPayments.Add(new RecurringPayment
        {
            Name = "Netflix",
            Amount = 15m,
            Category = "Entertainment",
            LedgerCategory = "Rewards",
            Active = true
        });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Here are your subscriptions."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("show my subscriptions", []));

        Assert.Contains("Netflix", handler.LastUserContent);
        Assert.DoesNotContain("\"amount\":15", handler.LastUserContent);
    }

    // ---------- Layer: returned action validation ----------

    [Fact]
    public async Task ChatAsync_DisallowedActionType_IsFilteredOut()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Chat("Done.", actionsJson: "[{\"type\":\"deleteEverything\",\"payload\":{}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("open the dashboard", []));

        Assert.Empty(outcome.Response.Actions);
    }

    [Fact]
    public async Task ChatAsync_ValidNavigationAction_IsKept()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Chat("Opening the dashboard.", actionsJson: "[{\"type\":\"openDashboard\",\"payload\":{}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("open the dashboard", []));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("openDashboard", action.Type);
    }

    [Fact]
    public async Task ChatAsync_MoreThanThreeActions_AreCappedAtThree()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        await context.SaveChangesAsync();
        var four = "[" + string.Join(",", Enumerable.Repeat("{\"type\":\"openDashboard\",\"payload\":{}}", 4)) + "]";
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Opening.", actionsJson: four));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("open the dashboard", []));

        Assert.True(outcome.Response.Actions.Count <= 3);
    }

    [Fact]
    public async Task ChatAsync_OpenLedgerWithUnknownCategory_IsRejected()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Chat("Filtering.", actionsJson: "[{\"type\":\"openLedger\",\"payload\":{\"category\":\"NotACategory\"}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("show me the ledger for NotACategory", []));

        Assert.Empty(outcome.Response.Actions);
    }

    [Fact]
    public async Task ChatAsync_QuestionOnlyRequest_StripsOpenLedgerAction()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.Add(Txn("t", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Chat("You spent 80.", actionsJson: "[{\"type\":\"openLedger\",\"payload\":{\"month\":\"Jul\",\"year\":2026}}]"));
        var service = NewService(context, handler);

        // A pure question must not silently navigate the user away from their screen.
        var outcome = await service.ChatAsync(new AiChatRequest("How much did I spend this month?", []));

        Assert.Empty(outcome.Response.Actions);
    }

    [Fact]
    public async Task ChatAsync_SensitiveMode_StripsAmountFromAddDraftPayload()
    {
        await using var context = NewContextWithSettings(hideSensitive: true);
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Chat(
                "Opening a draft.",
                actionsJson: "[{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Lunch\",\"amount\":20,\"category\":\"Food\",\"txType\":\"outflow\"}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("add a food transaction for lunch", []));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("openAddLedgerDraft", action.Type);
        Assert.False(action.Payload.ContainsKey("amount"));
    }

    // ---------- Layer: deterministic ledger-edit resolution (zero model calls) ----------

    [Fact]
    public async Task ChatAsync_SensitiveModeEdit_RefusesWithoutCallingModel()
    {
        await using var context = NewContextWithSettings(hideSensitive: true);
        context.Transactions.Add(Txn("prev", new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc), "Old Cafe", -18));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler();
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("Edit Old Cafe in the previous cycle and change amount to 25", []));

        Assert.Equal(0, handler.CallCount);
        Assert.Contains("Unhide balances", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_EditWithMultipleMatches_AsksForClarificationWithoutCallingModel()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.AddRange(
            Txn("cafe-a", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), "Cafe Aroma", -18),
            Txn("cafe-b", new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc), "Cafe Bean", -22));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler();
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("edit Cafe in the previous cycle", []));

        Assert.Equal(0, handler.CallCount);
        Assert.Empty(outcome.Response.Actions);
        Assert.Contains("multiple matches", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_EditWithNoMatch_ReportsNotFoundWithoutCallingModel()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.Add(Txn("cafe", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), "Cafe Aroma", -18));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler();
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("edit Starbucks in the previous cycle", []));

        Assert.Equal(0, handler.CallCount);
        Assert.Contains("couldn't find", outcome.Response.Reply);
    }

    // ---------- Layer: prompt scoping (Phase 8) ----------

    [Fact]
    public async Task ChatAsync_WishlistQuestion_OmitsUnrelatedRecurringAndTransactionData()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.WishlistItems.Add(new WishlistItem { Id = 1, Name = "Camera", Price = 500, IsActive = true, IsPurchased = false });
        context.RecurringPayments.Add(new RecurringPayment { Name = "Netflix", Amount = 15m, Category = "Entertainment", LedgerCategory = "Rewards", Active = true });
        context.Transactions.Add(Txn("g", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Here is your wishlist."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("what is on my wishlist", []));

        Assert.Contains("Camera", handler.LastUserContent);
        Assert.DoesNotContain("Netflix", handler.LastUserContent);
        Assert.DoesNotContain("Groceries", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_CountQuestion_OmitsWishlistAndRecurringData()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.WishlistItems.Add(new WishlistItem { Id = 1, Name = "Camera", Price = 500, IsActive = true, IsPurchased = false });
        context.RecurringPayments.Add(new RecurringPayment { Name = "Netflix", Amount = 15m, Category = "Entertainment", LedgerCategory = "Rewards", Active = true });
        context.Transactions.Add(Txn("b", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), "Badminton court", -12));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("You played once."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("how many badminton did I play last cycle", []));

        Assert.Contains("transactionMatches", handler.LastUserContent);
        Assert.DoesNotContain("Camera", handler.LastUserContent);
        Assert.DoesNotContain("Netflix", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_NavigationRequest_DoesNotAttachLargeDataBlocks()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.WishlistItems.Add(new WishlistItem { Id = 1, Name = "Camera", Price = 500, IsActive = true, IsPurchased = false });
        context.Transactions.Add(Txn("g", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Opening.", actionsJson: "[{\"type\":\"openDashboard\",\"payload\":{}}]"));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("open the dashboard please", []));

        // A pure navigation request needs no financial datasets at all.
        Assert.DoesNotContain("Camera", handler.LastUserContent);
        Assert.DoesNotContain("Groceries", handler.LastUserContent);
        Assert.DoesNotContain("\"budgetTargets\"", handler.LastUserContent);
    }

    // ---------- Layer: deterministic constraints (Phase 2) ----------

    [Fact]
    public async Task ChatAsync_PreventNavigation_DropsNavigationActions()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.Add(Txn("t", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Chat("Your total is 80.", actionsJson: "[{\"type\":\"openDashboard\",\"payload\":{}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("Don't open the dashboard, just tell me my total this month", []));

        Assert.Empty(outcome.Response.Actions);
    }

    [Fact]
    public async Task ChatAsync_WithoutTransfers_ExcludesTransferRowsFromPrompt()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.AddRange(
            new Transaction { Id = "spend", Date = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), Description = "Groceries", Category = "Food", LedgerCategory = "Essentials", Amount = -80 },
            new Transaction { Id = "xfer", Date = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc), Description = "Move To Savings", Category = "Transfer", LedgerCategory = "Transfer:Stability", Amount = -500 });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Here are your transactions."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("What transactions did I have this month without counting transfers", []));

        Assert.Contains("Groceries", handler.LastUserContent);
        Assert.DoesNotContain("Move To Savings", handler.LastUserContent);
        Assert.Contains("\"excludeTransfers\":true", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_ExcludingCategory_RemovesThatCategoryFromPrompt()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.TransactionCategories.Add(new TransactionCategory { Id = "rent", Name = "Rent" });
        context.Transactions.AddRange(
            new Transaction { Id = "food", Date = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), Description = "Groceries", Category = "Food", LedgerCategory = "Essentials", Amount = -80 },
            new Transaction { Id = "rent", Date = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc), Description = "Monthly Rent", Category = "Rent", LedgerCategory = "Essentials", Amount = -1200 });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Here are your transactions."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("What transactions did I have this month excluding rent", []));

        Assert.Contains("Groceries", handler.LastUserContent);
        Assert.DoesNotContain("Monthly Rent", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_HypotheticalEdit_DoesNotResolveEditActionAndReachesModel()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.Add(Txn("cafe", new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc), "Old Cafe", -18));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Hypothetically, that would change your total."));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("What if I change Old Cafe in the previous cycle to 25?", []));

        Assert.True(handler.CallCount >= 1);
        Assert.DoesNotContain(outcome.Response.Actions, a => a.Type.StartsWith("openEdit"));
    }

    [Fact]
    public async Task ChatAsync_NegatedWishlistTopic_OmitsWishlistFromPrompt()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.WishlistItems.Add(new WishlistItem { Id = 3, Name = "Fancy Camera", Price = 500, IsActive = true, IsPurchased = false });
        context.Transactions.Add(Txn("t", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Your total is 80."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("How much did I spend this month? I'm not asking about my wishlist", []));

        Assert.DoesNotContain("Fancy Camera", handler.LastUserContent);
    }

    // ---------- Layer: conversation state round-trip (Phase 4) ----------

    [Fact]
    public async Task ChatAsync_ClientState_DrivesQueryWithoutHistoryText()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.Add(Txn("badminton", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), "Badminton court", -12));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("You did it 1 time."));
        var service = NewService(context, handler);

        // No history text at all -- only structured state carries the topic + cycle.
        var outcome = await service.ChatAsync(new AiChatRequest(
            "How many did I do?",
            [],
            new AiConversationState("ledger.activity_count", "badminton", "last cycle", null)));

        Assert.Contains("Badminton court", handler.LastUserContent);
        Assert.Contains("\"month\":\"Jun\"", handler.LastUserContent);
        Assert.Contains("\"searchText\":\"badminton\"", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_PriorMatchedIdsDriveFollowUpMetrics()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.AddRange(
            Txn("chosen-1", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Badminton court", -12),
            Txn("chosen-2", new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc), "Badminton shuttle", -8),
            Txn("other", new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc), "Badminton unrelated", -99));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Those cost 20."));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest(
            "How much did those cost?", [],
            new AiConversationState("ledger.activity_count", "badminton", "this month", null,
                LastMatchedTransactionIds: ["chosen-1", "chosen-2"])));

        Assert.DoesNotContain("Badminton unrelated", handler.LastUserContent);
        Assert.Contains("Badminton court", handler.LastUserContent);
        Assert.Contains("Badminton shuttle", handler.LastUserContent);
        Assert.NotNull(outcome.Response);
    }

    [Fact]
    public async Task ChatAsync_PriorMatchedIdsProvideHighestMetric()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.AddRange(
            Txn("one", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Court", -12),
            Txn("two", new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc), "Racket", -40),
            Txn("other", new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc), "Other", -99));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Racket was highest at 40."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest(
            "Which of those was highest?", [],
            new AiConversationState("ledger.activity_count", "", "this month", null,
                LastMatchedTransactionIds: ["one", "two"])));

        Assert.Contains("\"highest\":", handler.LastUserContent);
        Assert.Contains("Racket", handler.LastUserContent);
        Assert.DoesNotContain("Other", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_PriorSingleIdResolvesEditFollowUp()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.Add(Txn("one", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Court", -12));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler();
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest(
            "Change that to Rewards", [],
            new AiConversationState("ledger.transaction_list", "court", "this month", null,
                LastMatchedTransactionIds: ["one"])));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("openEditLedgerDraft", action.Type);
        Assert.Equal("one", action.Payload["id"]);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ChatAsync_MoreThanTransactionCapKeepsExactCountAndBoundedPromptSample()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        for (var i = 0; i < 2001; i++)
        {
            context.Transactions.Add(Txn($"badminton-{i}", new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(i), "Badminton", -1));
        }
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("You had 2001 sessions."));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("How many badminton did I have this month?", []));

        Assert.False(outcome.IsProviderError);
        Assert.Contains("\"count\":2001", handler.LastUserContent);
        Assert.Contains("\"complete\":true", handler.LastUserContent);
        Assert.DoesNotContain("badminton-0", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_ExactlyTransactionCapIsNotMarkedTruncated()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        for (var i = 0; i < 2000; i++)
        {
            context.Transactions.Add(Txn($"exact-{i}", new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(i), "Badminton", -1));
        }
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("You had 2000 sessions."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("How many badminton did I have this month?", []));

        Assert.Contains("\"count\":2000", handler.LastUserContent);
        Assert.Contains("\"complete\":true", handler.LastUserContent);
        Assert.DoesNotContain("\"aggregatesTruncated\":true", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_CycleTotalTruncated_RecoversExactOutflowAndStaysNonApproximate()
    {
        // Phase 4 pipeline: first pass marks cycleSummaries Truncated/not-exact, the validator
        // flags it Recoverable, a SQL SUM recovers the true total, and the second pass must both
        // report the exact figure AND stop the sufficiency gate from forcing approximate wording.
        await using var context = NewContextWithSettings();
        for (var i = 0; i < 2001; i++)
        {
            context.Transactions.Add(Txn($"rent-{i}", new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(i), "Rent", -1));
        }
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("You spent 2001 this month."));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("How much did I spend this month?", []));

        Assert.False(outcome.IsProviderError);
        Assert.Contains("\"aggregatesTruncated\":true", handler.LastUserContent);
        Assert.Contains("\"recoveredExactOutflow\":2001", handler.LastUserContent);
        Assert.Contains("\"isApproximate\":false", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_ResponseCarriesOutgoingState()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.Add(Txn("t", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("You spent 80."));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("How much did I spend this month?", []));

        Assert.NotNull(outcome.Response.State);
        Assert.Equal("Jul 2026", outcome.Response.State!.LastResolvedCycle);
    }

    [Fact]
    public async Task ChatAsync_TamperedClientState_IsSanitizedAndIdsAreReDerived()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.Add(Txn("real", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Starbucks", -8));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Found 1."));
        var service = NewService(context, handler);

        var tampered = new AiConversationState(
            LastIntent: "totally.fake.intent",
            LastSearchText: "Starbucks",
            LastCycleHint: "this month",
            LastWishlistReference: null,
            LastMatchedTransactionIds: ["ghost-id-that-does-not-exist"]);

        var outcome = await service.ChatAsync(new AiChatRequest("Show Starbucks spending", [], tampered));

        // The fabricated id must never survive: outgoing ids are the rows the DB actually returned.
        Assert.NotNull(outcome.Response.State);
        Assert.DoesNotContain("ghost-id-that-does-not-exist", outcome.Response.State!.LastMatchedTransactionIds ?? []);
        Assert.Contains("real", outcome.Response.State!.LastMatchedTransactionIds ?? []);
    }

    [Fact]
    public async Task ChatAsync_NewIndependentQuestion_OverridesPriorStateCycle()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.AddRange(
            Txn("mar", new DateTime(2023, 3, 10, 12, 0, 0, DateTimeKind.Utc), "March Coffee", -15),
            Txn("jul", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "July Lunch", -20));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("You spent 15."));
        var service = NewService(context, handler);

        // Prior state points at a different cycle/topic; the new self-contained question wins.
        var outcome = await service.ChatAsync(new AiChatRequest(
            "How much did I spend on food in March 2023?",
            [],
            new AiConversationState("ledger.activity_count", "badminton", "this cycle", null)));

        Assert.Contains("\"month\":\"Mar\"", handler.LastUserContent);
        Assert.Equal("Mar 2023", outcome.Response.State!.LastResolvedCycle);
    }

    // ---------- Layer: provider error propagation ----------

    [Fact]
    public async Task ChatAsync_ProviderPersistentlyUnavailable_ReportsProviderError()
    {
        await using var context = NewContextWithSettings();
        context.Transactions.Add(Txn("t", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(); // configured below to always fail
        handler.AlwaysFailWith = HttpStatusCode.ServiceUnavailable;
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("How much did I spend this month?", []));

        Assert.True(outcome.IsProviderError);
    }

    // ---------- helpers ----------

    private static AppDbContext NewContextWithSettings(bool hideSensitive = false)
    {
        var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            HideSensitive = hideSensitive,
            Currency = "USD",
            EssentialsAlloc = 0.5m,
            GrowthAlloc = 0.25m,
            StabilityAlloc = 0.15m,
            RewardsAlloc = 0.1m
        });
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.SaveChanges();
        return context;
    }

    private static AiAssistantService NewService(AppDbContext context, ScriptedAiHandler handler)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")),
            NullLogger<AiClient>.Instance);
        return new AiAssistantService(client, context, new TransactionCategoryService(context, cache));
    }

    private static Transaction Txn(string id, DateTime date, string description, decimal amount) => new()
    {
        Id = id,
        Date = date,
        Description = description,
        Category = "Food",
        LedgerCategory = "Essentials",
        Amount = amount
    };

    // Records every request and returns queued model replies in order. When the queue is down
    // to its last entry that entry is reused, so single-call tests need supply only one reply.
    // Classifier and chat calls share the same HTTP path; WasClassifierCall distinguishes them
    // by the classifier prompt's fixed prefix.
    private sealed class ScriptedAiHandler : HttpMessageHandler
    {
        private readonly Queue<string> _replies;
        public List<string> UserContents { get; } = new();
        public int CallCount => UserContents.Count;
        public string LastUserContent => UserContents[^1];
        public HttpStatusCode? AlwaysFailWith { get; set; }

        public ScriptedAiHandler(params string[] replies)
            => _replies = new Queue<string>(replies.Length == 0 ? new[] { Chat("ok") } : replies);

        public static string Chat(string reply, string actionsJson = "[]", bool close = false)
            => $"{{\"reply\":{JsonSerializer.Serialize(reply)},\"closeChat\":{close.ToString().ToLowerInvariant()},\"actions\":{actionsJson}}}";

        public static string Classifier(string[] intents, double confidence, string? searchText = null, string? cycleHint = null)
            => JsonSerializer.Serialize(new { intents, confidence, searchText, cycleHint });

        public bool WasClassifierCall(int index) => UserContents[index].StartsWith("Classify the user's", StringComparison.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            UserContents.Add(document.RootElement
                .GetProperty("contents")[0]
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? string.Empty);

            if (AlwaysFailWith is { } status)
            {
                return new HttpResponseMessage(status);
            }

            var reply = _replies.Count > 1 ? _replies.Dequeue() : _replies.Peek();
            var providerBody = JsonSerializer.Serialize(new
            {
                candidates = new[]
                {
                    new
                    {
                        content = new { parts = new[] { new { text = reply } } },
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
