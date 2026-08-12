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

    [Fact]
    public async Task ChatAsync_DeleteCommand_ReturnsConfirmationRequestForKnownRecord()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.Add(Txn("coffee", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Coffee", -8));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "I'll open the delete confirmation.",
            actionsJson: "[{\"type\":\"requestDeleteLedger\",\"payload\":{\"id\":\"coffee\"}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("delete my coffee transaction", []));

        Assert.True(handler.CallCount >= 1);
        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("requestDeleteLedger", action.Type);
        Assert.Equal("coffee", action.Payload["id"]?.ToString());
    }

    [Fact]
    public async Task ChatAsync_DeleteCommand_IsBlockedInSensitiveModeWithoutCallingModel()
    {
        await using var context = NewContextWithSettings(hideSensitive: true);
        context.Transactions.Add(Txn("coffee", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Coffee", -8));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler();
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("delete my coffee transaction", []));

        Assert.Equal(0, handler.CallCount);
        Assert.Empty(outcome.Response.Actions);
        Assert.Contains("Sensitive mode", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_LoanPreset_LoadsOnlyTheSelectedSavedLoan()
    {
        await using var context = NewContextWithSettings();
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "bill-home", Name = "Home payment", Amount = -100m, Frequency = "Monthly",
            Category = "Bills", LedgerCategory = "Essentials", DueDate = 1,
            StartDate = "2026-01-01", Active = true
        });
        context.Loans.Add(new Loan
        {
            Id = "loan-home", Name = "Home loan", RecurringPaymentId = "bill-home",
            OpeningPrincipal = 1000m, TrackingStartDate = new DateOnly(2026, 1, 1),
            AnnualRatePercent = 5m, TermPeriods = 12, InterestMethod = LoanInterestMethod.ReducingBalance,
            ScheduleFrequency = "Monthly", ScheduleDueDay = 1,
            ScheduleStartDate = new DateOnly(2026, 1, 1), ScheduleStatus = LoanScheduleStatus.Complete
        });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Here is the saved loan summary."));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest(
            "Explain this loan",
            [],
            Context: new AiInvocationContext("recurring", "loan-explain", LoanId: "loan-home")));

        Assert.False(outcome.IsProviderError);
        Assert.Contains("\"loan.summary\"", handler.LastUserContent);
        Assert.Contains("\"id\":\"loan-home\"", handler.LastUserContent);
        Assert.Contains("\"amountStillOwed\":1000", handler.LastUserContent);
        Assert.Equal("loan-home", outcome.Response.State?.LastLoanId);
    }

    [Fact]
    public async Task ChatAsync_IncompleteLoan_DoesNotExposeSpeculativeForecasts()
    {
        await using var context = NewContextWithSettings();
        context.Loans.Add(new Loan
        {
            Id = "loan-orphan", Name = "Old loan", RecurringPaymentId = "missing-bill",
            OpeningPrincipal = 1000m, TrackingStartDate = new DateOnly(2026, 1, 1),
            AnnualRatePercent = 5m, TermPeriods = 12, InterestMethod = LoanInterestMethod.ReducingBalance,
            ScheduleStatus = LoanScheduleStatus.Incomplete
        });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("The forecast is unavailable."));
        var service = NewService(context, handler);

        _ = await service.ChatAsync(new AiChatRequest(
            "Explain this loan",
            [],
            Context: new AiInvocationContext("recurring", "loan-explain", LoanId: "loan-orphan")));

        Assert.Contains("\"forecastAvailable\":false", handler.LastUserContent);
        Assert.DoesNotContain("amountStillOwed", handler.LastUserContent);
        Assert.DoesNotContain("payoffDate", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_LoanAmounts_AreBlockedBySensitiveModeBeforeGeneration()
    {
        await using var context = NewContextWithSettings(hideSensitive: true);
        context.Loans.Add(new Loan
        {
            Id = "loan-private", Name = "Private loan", RecurringPaymentId = "missing-bill",
            OpeningPrincipal = 1000m, TrackingStartDate = new DateOnly(2026, 1, 1),
            AnnualRatePercent = 5m, TermPeriods = 12, InterestMethod = LoanInterestMethod.ReducingBalance,
            ScheduleStatus = LoanScheduleStatus.Incomplete
        });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler();
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest(
            "How much do I owe?",
            [],
            Context: new AiInvocationContext("recurring", "loan-explain", LoanId: "loan-private")));

        Assert.Equal(0, handler.CallCount);
        Assert.Contains("Sensitive mode", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_LinkedRecurringDeleteAction_IsSuppressed()
    {
        await using var context = NewContextWithSettings();
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "bill-home", Name = "Home payment", Amount = -100m, Frequency = "Monthly",
            Category = "Bills", LedgerCategory = "Essentials", DueDate = 1,
            StartDate = "2026-01-01", Active = true
        });
        context.Loans.Add(new Loan
        {
            Id = "loan-home", Name = "Home loan", RecurringPaymentId = "bill-home",
            OpeningPrincipal = 1000m, TrackingStartDate = new DateOnly(2026, 1, 1),
            AnnualRatePercent = 5m, TermPeriods = 12, InterestMethod = LoanInterestMethod.ReducingBalance,
            ScheduleFrequency = "Monthly", ScheduleDueDay = 1,
            ScheduleStartDate = new DateOnly(2026, 1, 1), ScheduleStatus = LoanScheduleStatus.Complete
        });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "I'll open delete.",
            "[{\"type\":\"requestDeleteRecurring\",\"payload\":{\"id\":\"bill-home\"}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("Delete my Home payment bill", []));

        Assert.Empty(outcome.Response.Actions);
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
        Assert.Equal("Here is some advice.", outcome.Response.Reply);
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
        // No OpenAiApiKey -> IsConfigured is false.
        var handler = new ScriptedAiHandler();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("OpenAiModel", "test-model")),
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
    public async Task ChatAsync_SettingsNavigation_IsAlwaysFilteredOut()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Chat("Opening settings.", actionsJson: "[{\"type\":\"openSettings\",\"payload\":{}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("open settings", []));

        Assert.Empty(outcome.Response.Actions);
    }

    [Fact]
    public async Task ChatAsync_SensitiveMode_FiltersProviderMutationAction()
    {
        await using var context = NewContextWithSettings(hideSensitive: true);
        context.Transactions.Add(Txn("coffee", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Coffee", -8));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Deleting.", actionsJson: "[{\"type\":\"requestDeleteLedger\",\"payload\":{\"id\":\"coffee\"}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("show my coffee transaction", []));

        Assert.Empty(outcome.Response.Actions);
    }

    [Fact]
    public async Task ChatAsync_RecurringToggle_RequiresKnownIdAndExplicitState()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "netflix", Name = "Netflix", Amount = 20, Frequency = "Monthly",
            Category = "Food", LedgerCategory = "Essentials", NextDueDate = "2026-07-15",
            DueDate = 15, StartDate = "2026-01-15", Active = true
        });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Turning it off.", actionsJson: "[{\"type\":\"toggleRecurring\",\"payload\":{\"id\":\"netflix\",\"active\":false}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("turn off my Netflix subscription", []));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("toggleRecurring", action.Type);
        Assert.Equal("netflix", action.Payload["id"]?.ToString());
    }

    [Fact]
    public async Task ChatAsync_TransferDraft_RequiresExplicitDistinctBuckets()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Opening transfer draft.",
            actionsJson: "[{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Move funds\",\"amount\":100,\"txType\":\"transfer\",\"ledgerCategorySpecified\":true,\"transferSource\":\"Growth\",\"transferTarget\":\"Stability\"}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("transfer 100 from Growth to Stability", []));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("Growth", action.Payload["transferSource"]?.ToString());
        Assert.Equal("Stability", action.Payload["transferTarget"]?.ToString());
    }

    [Fact]
    public async Task ChatAsync_InflowDraft_KeepsInflowTypeAndNormalCategory()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Opening inflow draft.",
            actionsJson: "[{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Cashback\",\"amount\":25,\"txType\":\"inflow\",\"category\":\"Food\",\"ledgerCategory\":\"Income\",\"ledgerCategorySpecified\":false}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("add a 25 cashback inflow", []));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("openAddLedgerDraft", action.Type);
        Assert.Equal("inflow", action.Payload["txType"]?.ToString());
        Assert.Equal("Food", action.Payload["category"]?.ToString());
        // An inflow defaults to the Income ledger so the staged draft has the same shape
        // the manual form produces for a positive amount (and can drive the auto-split).
        Assert.Equal("Income", action.Payload["ledgerCategory"]?.ToString());
    }

    [Fact]
    public async Task ChatAsync_OutflowDraft_StillDefaultsToEssentials()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Opening outflow draft.",
            actionsJson: "[{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Lunch\",\"amount\":25,\"txType\":\"outflow\",\"category\":\"Food\",\"ledgerCategory\":\"Income\",\"ledgerCategorySpecified\":true}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("add a 25 lunch", []));

        var action = Assert.Single(outcome.Response.Actions);
        // Income is rejected on an outflow, so the unspecified default applies.
        Assert.Equal("Essentials", action.Payload["ledgerCategory"]?.ToString());
        Assert.Equal(false, action.Payload["ledgerCategorySpecified"]);
    }

    [Fact]
    public async Task ChatAsync_SingleLineLedgerShorthand_UsesDraftSchemaAndReturnsAutomaticDraftAction()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Staging Badminton for review.",
            actionsJson: "[{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Badminton\",\"amount\":10,\"txType\":\"outflow\",\"category\":\"Hobbies\",\"ledgerCategory\":\"Essentials\",\"ledgerCategorySpecified\":false}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("Badminton 10", []));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("openAddLedgerDraft", action.Type);
        Assert.Equal("Badminton", action.Payload["description"]?.ToString());
        Assert.Equal("10", action.Payload["amount"]?.ToString());
        Assert.Equal("outflow", action.Payload["txType"]?.ToString());
        Assert.Equal("Essentials", action.Payload["ledgerCategory"]?.ToString());
        Assert.Contains("ledger.add", handler.LastUserContent);
        using var request = JsonDocument.Parse(handler.RequestBodies[^1]);
        var actionsSchema = request.RootElement.GetProperty("text")
            .GetProperty("format")
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("actions");
        Assert.Equal(1, actionsSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(1, actionsSchema.GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public async Task ChatAsync_AddedUpAmountOnOneLine_StillPinsTheResponseToOneDraftAction()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Staging Mamak.",
            actionsJson: "[{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Mamak\",\"amount\":20.30,\"txType\":\"outflow\",\"category\":\"Food\",\"ledgerCategory\":\"Essentials\",\"ledgerCategorySpecified\":false}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("And Mamak 18+2.30", []));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("Mamak", action.Payload["description"]?.ToString());
        using var request = JsonDocument.Parse(handler.RequestBodies[^1]);
        var actionsSchema = request.RootElement.GetProperty("text")
            .GetProperty("format")
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("actions");
        // A sum is one record, so the schema must still forbid an empty actions array -- that is
        // what stopped the model from answering with a staging sentence and no draft behind it.
        Assert.Equal(1, actionsSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(1, actionsSchema.GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public async Task ChatAsync_StagingClaimWithNoAction_IsReplacedWithAnHonestReply()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Staging 1 draft: Mamak for MYR 20.30.",
            actionsJson: "[]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("And Mamak 18+2.30", []));

        Assert.Empty(outcome.Response.Actions);
        Assert.DoesNotContain("Staging", outcome.Response.Reply);
        Assert.Contains("Nothing was added", outcome.Response.Reply);
        Assert.False(outcome.Response.CloseChat);
    }

    [Fact]
    public async Task ChatAsync_StagingClaimWhoseActionSensitiveModeDropped_SaysNothingWasAdded()
    {
        await using var context = NewContextWithSettings(hideSensitive: true);
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Staging a draft for lunch.",
            actionsJson: "[{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Lunch\",\"amount\":20,\"category\":\"Food\",\"txType\":\"outflow\"}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("add a food transaction for lunch", []));

        Assert.Empty(outcome.Response.Actions);
        Assert.DoesNotContain("Staging", outcome.Response.Reply);
        Assert.Contains("Unhide balances", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_DraftClarificationQuestion_KeepsItsOwnWording()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Do you want me to open a draft for that, or edit the existing one?",
            actionsJson: "[]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("add something about lunch", []));

        // An offer is not a claim, so the honesty guard must leave a clarification alone.
        Assert.Contains("Do you want me to open a draft", outcome.Response.Reply);
    }

    [Fact]
    public async Task ChatAsync_MultiRecordLedgerAdd_KeepsEveryFlatDraftAction()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Staging two drafts.",
            actionsJson: "[{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Nasi Lemak\",\"amount\":12,\"txType\":\"outflow\",\"category\":\"Food\",\"ledgerCategory\":\"Essentials\",\"ledgerCategorySpecified\":false}},{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Car Fuel\",\"amount\":30,\"txType\":\"outflow\",\"category\":\"Food\",\"ledgerCategory\":\"Growth\",\"ledgerCategorySpecified\":true}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("Nasi Lemak 12\nCar Fuel 30", []));

        Assert.Equal(2, outcome.Response.Actions.Count);
        Assert.All(outcome.Response.Actions, action => Assert.Equal("openAddLedgerDraft", action.Type));
        Assert.Equal("Nasi Lemak", outcome.Response.Actions[0].Payload["description"]?.ToString());
        Assert.Equal("Car Fuel", outcome.Response.Actions[1].Payload["description"]?.ToString());
        Assert.Contains("ledger.add", handler.LastUserContent);
        using var request = JsonDocument.Parse(handler.RequestBodies[^1]);
        var actionsSchema = request.RootElement.GetProperty("text")
            .GetProperty("format")
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("actions");
        Assert.Equal(2, actionsSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(2, actionsSchema.GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public async Task ChatAsync_SubscriptionCostQuestionUsesSchemaWithoutReminderEditFields()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "spotify",
            Name = "Spotify",
            Amount = 15,
            Frequency = "Monthly",
            Category = "Entertainment",
            LedgerCategory = "Rewards",
            NextDueDate = "2026-07-20",
            DueDate = 20,
            StartDate = "2026-01-01",
            Active = true
        });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Your subscriptions cost 15 per month."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("How much do my subscriptions cost me a month?", []));

        using var request = JsonDocument.Parse(handler.RequestBodies[^1]);
        var payloadProperties = request.RootElement.GetProperty("text")
            .GetProperty("format")
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("actions")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("payload")
            .GetProperty("properties");
        Assert.False(payloadProperties.TryGetProperty("enabled", out _));
        Assert.False(payloadProperties.TryGetProperty("reminderMode", out _));
        Assert.False(payloadProperties.TryGetProperty("leadDays", out _));
    }

    [Fact]
    public async Task ChatAsync_LedgerAdd_DefaultsMissingLedgerMetadataToEssentials()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Staging a draft.",
            actionsJson: "[{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Lunch\",\"amount\":12,\"txType\":\"outflow\",\"category\":\"Food\",\"ledgerCategory\":\"Growth\",\"date\":\"\",\"transferSource\":\"\",\"price\":\"\"}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("add lunch 12", []));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("openAddLedgerDraft", action.Type);
        Assert.Equal("Essentials", action.Payload["ledgerCategory"]?.ToString());
        Assert.False(Assert.IsType<bool>(action.Payload["ledgerCategorySpecified"]));
    }

    [Fact]
    public async Task ChatAsync_MultiRecordLedgerAdd_AllowsMoreThanThreeFlatDrafts()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        var actionsJson = "[" + string.Join(",", Enumerable.Range(1, 4).Select(index =>
            $"{{\"type\":\"openAddLedgerDraft\",\"payload\":{{\"description\":\"Item {index}\",\"amount\":{index},\"txType\":\"outflow\",\"category\":\"Food\",\"ledgerCategory\":\"Essentials\",\"ledgerCategorySpecified\":false}}}}")) + "]";
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Staging four drafts.", actionsJson));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("Item 1 1\nItem 2 2\nItem 3 3\nItem 4 4", []));

        Assert.Equal(4, outcome.Response.Actions.Count);
    }

    [Fact]
    public async Task ChatAsync_LedgerAdd_TrustsValidatedModelCategoryAndExplicitLedgerHintWithoutExtraProviderCall()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.TransactionCategories.Add(new TransactionCategory { Id = "transport", Name = "Transport" });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Chat(
                "Staging two drafts.",
                actionsJson: "[{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Nasi Lemak\",\"amount\":12,\"txType\":\"outflow\",\"category\":\"Food\",\"ledgerCategory\":\"Essentials\",\"ledgerCategorySpecified\":false}},{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Car Fuel\",\"amount\":30,\"txType\":\"outflow\",\"category\":\"Food\",\"ledgerCategory\":\"Essentials\",\"ledgerCategorySpecified\":false}}]"),
            "{\"suggestions\":[{\"category\":\"Food\",\"confidence\":0.99}]}");
        var service = NewService(context, handler, withCategorySuggestions: true);

        var outcome = await service.ChatAsync(new AiChatRequest("Nasi Lemak 12\nCar Fuel 30 Transport Growth", []));

        Assert.Equal(2, outcome.Response.Actions.Count);
        Assert.Equal("Food", outcome.Response.Actions[0].Payload["category"]?.ToString());
        Assert.Equal("Essentials", outcome.Response.Actions[0].Payload["ledgerCategory"]?.ToString());
        Assert.Equal("Transport", outcome.Response.Actions[1].Payload["category"]?.ToString());
        Assert.Equal("Growth", outcome.Response.Actions[1].Payload["ledgerCategory"]?.ToString());
        Assert.True(Assert.IsType<bool>(outcome.Response.Actions[1].Payload["ledgerCategorySpecified"]));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ChatAsync_WishlistPurchaseAction_RequiresAnUnpurchasedKnownItem()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        var rewards = Txn("rewards", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Rewards", 200);
        rewards.LedgerCategory = "Rewards";
        context.Transactions.Add(rewards);
        context.WishlistItems.Add(new WishlistItem
        {
            Id = 7, Name = "Headphones", Price = 200, Priority = "Medium",
            IsPurchased = false, IsActive = true, CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat(
            "Opening confirmation.", actionsJson: "[{\"type\":\"requestPurchaseWishlist\",\"payload\":{\"id\":\"7\"}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("claim my Headphones wishlist item", []));

        Assert.Equal("requestPurchaseWishlist", Assert.Single(outcome.Response.Actions).Type);
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
    public async Task ChatAsync_MoreThanFourActions_AreCappedAtFour()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        await context.SaveChangesAsync();
        var five = "[" + string.Join(",", Enumerable.Repeat("{\"type\":\"openDashboard\",\"payload\":{}}", 5)) + "]";
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Opening.", actionsJson: five));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("open the dashboard", []));

        Assert.Equal(4, outcome.Response.Actions.Count);
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
    public async Task ChatAsync_SensitiveMode_BlocksLedgerDraftCreation()
    {
        await using var context = NewContextWithSettings(hideSensitive: true);
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Chat(
                "Opening a draft.",
                actionsJson: "[{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Lunch\",\"amount\":20,\"category\":\"Food\",\"txType\":\"outflow\"}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("add a food transaction for lunch", []));

        Assert.Empty(outcome.Response.Actions);
    }

    [Fact]
    public async Task ChatAsync_SensitiveMode_BlocksSingleLineLedgerShorthand()
    {
        await using var context = NewContextWithSettings(hideSensitive: true);
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Chat(
                "Opening a draft.",
                actionsJson: "[{\"type\":\"openAddLedgerDraft\",\"payload\":{\"description\":\"Badminton\",\"amount\":10,\"category\":\"Hobbies\",\"txType\":\"outflow\"}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("Badminton 10", []));

        Assert.Empty(outcome.Response.Actions);
        Assert.Contains("Sensitive mode", outcome.Response.Reply);
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task ChatAsync_SensitiveMode_BlocksRecurringDraftCreation()
    {
        await using var context = NewContextWithSettings(hideSensitive: true);
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Chat(
                "Opening a draft.",
                actionsJson: "[{\"type\":\"openAddRecurringDraft\",\"payload\":{\"description\":\"Netflix\",\"amount\":15,\"category\":\"Entertainment\"}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("add netflix subscription for 15", []));

        Assert.Empty(outcome.Response.Actions);
    }

    [Fact]
    public async Task ChatAsync_SensitiveMode_BlocksWishlistDraftCreation()
    {
        await using var context = NewContextWithSettings(hideSensitive: true);
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(
            ScriptedAiHandler.Chat(
                "Opening a draft.",
                actionsJson: "[{\"type\":\"openAddWishlistDraft\",\"payload\":{\"name\":\"Car\",\"price\":50000}}]"));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("add car to my wishlist for 50000", []));

        Assert.Empty(outcome.Response.Actions);
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
        context.Transactions.Add(new Transaction
        {
            Id = "adjustment",
            Date = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc),
            Description = "Balance correction",
            Category = "Adjustment",
            LedgerCategory = "Essentials",
            Amount = -100
        });
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

    [Fact]
    public async Task ChatAsync_ProviderRateLimited_ReportsProviderError()
    {
        // When the upstream AI provider returns 429 (quota exhausted), the service must
        // surface IsProviderError=true so the controller maps it to HTTP 503 instead of 200.
        // This mirrors the 503/ServiceUnavailable case: any non-OK provider response should
        // never present as a successful chat outcome to the frontend.
        await using var context = NewContextWithSettings();
        context.Transactions.Add(Txn("t", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler();
        handler.AlwaysFailWith = HttpStatusCode.TooManyRequests;
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("How much did I spend this month?", []));

        Assert.True(outcome.IsProviderError);
    }

    [Fact]
    public async Task ChatAsync_ProviderRateLimited_ReturnsNonEmptyReply()
    {
        // Even under a 429 from the AI provider the client must receive a human-readable
        // reply — not null, empty, or a raw status code — so the chat UI always has
        // something to render rather than a blank bubble.
        await using var context = NewContextWithSettings();
        context.Transactions.Add(Txn("t", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Groceries", -80));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler();
        handler.AlwaysFailWith = HttpStatusCode.TooManyRequests;
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("How much did I spend this month?", []));

        Assert.False(string.IsNullOrWhiteSpace(outcome.Response.Reply));
    }

    // ---------- Layer: gap-filling derived metrics ----------

    [Fact]
    public async Task ChatAsync_AmountThresholdQuestion_IncludesThresholdMatches()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.AddRange(
            Txn("tv", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "New TV", -300),
            Txn("coffee", new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc), "Coffee", -8));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Your TV exceeded 250."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("which transaction exceeded 250 this month?", []));

        Assert.Contains("thresholdMatches", handler.LastUserContent);
        Assert.Contains("New TV", handler.LastUserContent);
        Assert.Contains("\"count\":1", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_LedgerBalanceForecastQuestion_IncludesForecast()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.AddRange(
            Txn("g1", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), "Growth deposit", 1000),
            Txn("g2", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "Growth deposit", 1000));
        // Route the Growth transactions into the Growth ledger.
        foreach (var t in context.Transactions) t.LedgerCategory = "Growth";
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("At your current pace..."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("how long until my growth reaches 5000?", []));

        Assert.Contains("ledgerBalanceForecast", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_RecurringCostQuestion_IncludesCostSummary()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.RecurringPayments.AddRange(
            new RecurringPayment { Id = "rp-1", Name = "Netflix", Amount = 12m, Category = "Entertainment", LedgerCategory = "Rewards", Active = true, Frequency = "Monthly" },
            new RecurringPayment { Id = "rp-2", Name = "Domain", Amount = 120m, Category = "Software", LedgerCategory = "Essentials", Active = true, Frequency = "Annually" });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Your subscriptions cost ..."));
        var service = NewService(context, handler);

        await service.ChatAsync(new AiChatRequest("how much do my subscriptions cost me a month?", []));

        Assert.Contains("recurringCostSummary", handler.LastUserContent);
        Assert.Contains("monthlyTotal", handler.LastUserContent);
    }

    [Fact]
    public async Task ChatAsync_SensitiveMode_OmitsThresholdMatches()
    {
        await using var context = NewContextWithSettings(hideSensitive: true);
        context.Transactions.Add(Txn("tv", new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc), "New TV", -300));
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Amounts are hidden."));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest("which transaction exceeded 250 this month?", []));

        Assert.Equal(0, handler.CallCount);
        Assert.Contains("Sensitive mode", outcome.Response.Reply);
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

    [Fact]
    public async Task ChatAsync_RewardsPlanPreset_LoadsCommitmentsPoolsRewardsAndForecastWithoutClassifier()
    {
        await using var context = NewContextWithSettings(hideSensitive: false);
        context.Transactions.AddRange(
            new Transaction { Id = "rewards-balance", Date = DateTime.UtcNow.AddDays(-2), Description = "Rewards", Category = "Rewards", LedgerCategory = "Rewards", Amount = 900m },
            new Transaction { Id = "essentials-balance", Date = DateTime.UtcNow.AddDays(-2), Description = "Essentials", Category = "Essentials", LedgerCategory = "Essentials", Amount = 1200m });
        context.SavingsGoals.AddRange(
            new SavingsGoal { Id = 41, Name = "Car service", TargetAmount = 600m, EarmarkedAmount = 200m, FundingBucket = SavingsGoalFundingBucket.Essentials, TargetDate = DateTime.UtcNow.AddMonths(2), Priority = "High", Status = SavingsGoalStatus.Active, CreatedAt = DateTime.UtcNow.AddMonths(-1) },
            new SavingsGoal { Id = 42, Name = "Annual cover", TargetAmount = 300m, EarmarkedAmount = 300m, FundingBucket = SavingsGoalFundingBucket.Rewards, TargetDate = DateTime.UtcNow.AddMonths(-1), Priority = "Medium", Status = SavingsGoalStatus.Completed, CompletedAt = DateTime.UtcNow.AddDays(-3), CreatedAt = DateTime.UtcNow.AddMonths(-3) },
            new SavingsGoal { Id = 43, Name = "Ready commitment", TargetAmount = 100m, EarmarkedAmount = 100m, FundingBucket = SavingsGoalFundingBucket.Rewards, TargetDate = DateTime.UtcNow.AddMonths(1), Priority = "Low", Status = SavingsGoalStatus.Active, CreatedAt = DateTime.UtcNow.AddMonths(-1) },
            new SavingsGoal { Id = 44, Name = "Overdue commitment", TargetAmount = 100m, EarmarkedAmount = 10m, FundingBucket = SavingsGoalFundingBucket.Rewards, TargetDate = DateTime.UtcNow.AddMonths(-2), Priority = "Low", Status = SavingsGoalStatus.Active, CreatedAt = DateTime.UtcNow.AddMonths(-3) },
            new SavingsGoal { Id = 45, Name = "On pace commitment", TargetAmount = 500m, EarmarkedAmount = 50m, FundingBucket = SavingsGoalFundingBucket.Rewards, TargetDate = DateTime.UtcNow.AddMonths(6), Priority = "Low", Status = SavingsGoalStatus.Active, CycleFundedKey = DateTime.UtcNow.ToString("yyyy-MM"), CycleFundedAmount = 500m, CreatedAt = DateTime.UtcNow.AddMonths(-1) });
        context.WishlistItems.AddRange(
            new WishlistItem { Id = 51, Name = "Headphones", Price = 250m, Priority = "High", IsActive = true, IsPurchased = false, CreatedAt = DateTime.UtcNow.AddDays(-5) },
            new WishlistItem { Id = 52, Name = "Weekend trip", Price = 400m, Priority = "Medium", IsActive = false, IsPurchased = false, CreatedAt = DateTime.UtcNow.AddDays(-4) },
            new WishlistItem { Id = 53, Name = "Coffee grinder", Price = 100m, Priority = "Low", IsActive = false, IsPurchased = true, PurchasedAt = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow.AddDays(-10) });
        await context.SaveChangesAsync();
        var handler = new ScriptedAiHandler(ScriptedAiHandler.Chat("Your plan is ready."));
        var service = NewService(context, handler);

        var outcome = await service.ChatAsync(new AiChatRequest(
            "Explain my plan",
            [],
            Context: new AiInvocationContext("wishlist", "rewards-plan", HasPendingLocalChanges: true)));

        Assert.Equal(1, handler.CallCount);
        Assert.False(handler.WasClassifierCall(0));
        Assert.Contains("rewards.summary", handler.LastUserContent);
        Assert.Contains("savings_goal.pacing", handler.LastUserContent);
        Assert.Contains("wishlist.forecast", handler.LastUserContent);
        Assert.Contains("\"pools\"", handler.LastUserContent);
        Assert.Contains("\"rewards\":{\"fundingBucket\":\"Rewards\"", handler.LastUserContent);
        Assert.Contains("\"essentials\":{\"fundingBucket\":\"Essentials\"", handler.LastUserContent);
        Assert.Contains("\"fundingBucket\":\"Essentials\"", handler.LastUserContent);
        Assert.Contains("\"status\":\"ready\"", handler.LastUserContent);
        Assert.Contains("\"status\":\"on-pace\"", handler.LastUserContent);
        Assert.Contains("\"status\":\"needs-funding\"", handler.LastUserContent);
        Assert.Contains("\"status\":\"overdue\"", handler.LastUserContent);
        Assert.Contains("\"status\":\"completed\"", handler.LastUserContent);
        Assert.Contains("\"status\":\"focused\"", handler.LastUserContent);
        Assert.Contains("\"status\":\"queued\"", handler.LastUserContent);
        Assert.Contains("\"status\":\"claimed\"", handler.LastUserContent);
        Assert.Contains("\"claimable\":", handler.LastUserContent);
        Assert.Contains("\"rewardForecast\":", handler.LastUserContent);
        Assert.Contains("does not include changes still syncing", outcome.Response.Reply);
    }

    private static AiAssistantService NewService(
        AppDbContext context,
        ScriptedAiHandler handler,
        bool withCategorySuggestions = false)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
            NullLogger<AiClient>.Instance);
        var categoryService = new TransactionCategoryService(context, cache);
        var suggestions = withCategorySuggestions
            ? new CategorySuggestionService(client, context, categoryService, cache, new CategoryCleanupApplier(context))
            : null;
        return new AiAssistantService(client, context, categoryService, suggestions);
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
        public List<string> RequestBodies { get; } = new();
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
            RequestBodies.Add(body);
            using var document = JsonDocument.Parse(body);
            UserContents.Add(document.RootElement
                .GetProperty("input")[0]
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString() ?? string.Empty);

            if (AlwaysFailWith is { } status)
            {
                return new HttpResponseMessage(status);
            }

            var reply = _replies.Count > 1 ? _replies.Dequeue() : _replies.Peek();
            var providerBody = JsonSerializer.Serialize(new
            {
                status = "completed",
                output = new[]
                {
                    new
                    {
                        type = "message",
                        content = new[] { new { type = "output_text", text = reply } }
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
