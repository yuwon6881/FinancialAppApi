using System.Net;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using static FinancialAppApi.Tests.AiTools.AiToolTestSupport;

namespace FinancialAppApi.Tests.AiTools;

// Behaviour Ask AI promises regardless of how the model finds its data: guards that never reach
// the provider, the safety rules every proposed action passes, draft placement and wording, and
// honest failure. Ported from the retired keyword-router suite onto the tool-calling engine.
public class AiChatBehaviorTests
{
    private static object Draft(object payload) => new { type = "openAddLedgerDraft", payload };

    // ---------- guards that answer without the provider ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankMessage_AsksForAQuestionWithoutCallingTheProvider(string message)
    {
        await using var db = await SeedAsync();
        using var harness = new AgentHarness(db, new ScriptedProvider());

        var outcome = await harness.AskAsync(message);

        Assert.Contains("Please ask", outcome.Response.Reply);
        Assert.Empty(harness.Provider.Requests);
    }

    [Fact]
    public async Task OverlongMessage_IsRejectedWithoutCallingTheProvider()
    {
        await using var db = await SeedAsync();
        using var harness = new AgentHarness(db, new ScriptedProvider());

        var outcome = await harness.AskAsync(new string('a', 2001));

        Assert.Contains("too long", outcome.Response.Reply);
        Assert.Empty(harness.Provider.Requests);
    }

    [Fact]
    public async Task GreetingAndFarewell_AreAnsweredLocallyAndAFarewellClosesTheChat()
    {
        await using var db = await SeedAsync();
        using var harness = new AgentHarness(db, new ScriptedProvider());

        var greeting = await harness.AskAsync("hi");
        var farewell = await harness.AskAsync("bye");

        Assert.False(string.IsNullOrWhiteSpace(greeting.Response.Reply));
        Assert.True(farewell.Response.CloseChat);
        Assert.Empty(harness.Provider.Requests);
    }

    [Fact]
    public async Task WithoutAnApiKey_ReportsThatAiIsNotConfigured()
    {
        await using var db = await SeedAsync();
        using var harness = new AgentHarness(db, new ScriptedProvider(), configured: false);

        var outcome = await harness.AskAsync("how much did I spend?");

        Assert.Equal("AI chat is not configured on the server.", outcome.Response.Reply);
        Assert.Empty(harness.Provider.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ProviderFailure_IsReportedAsAProviderErrorWithAReadableReply(HttpStatusCode status)
    {
        await using var db = await SeedAsync();
        using var harness = new AgentHarness(db, new ScriptedProvider { AlwaysFailWith = status });

        var outcome = await harness.AskAsync("how much did I spend this cycle?");

        Assert.True(outcome.IsProviderError);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Response.Reply));
    }

    // ---------- action safety ----------

    [Fact]
    public async Task ABillLinkedToALoanCannotBeDeletedFromChat()
    {
        await using var db = await SeedAsync();
        db.RecurringPayments.Add(new RecurringPayment
        {
            Id = "bill-home", Name = "Home payment", Amount = -100m, Frequency = "Monthly", Category = "Bills",
            LedgerCategory = "Essentials", DueDate = 1, StartDate = "2026-01-01", Active = true
        });
        db.Loans.Add(new Loan
        {
            Id = "loan-home", Name = "Home loan", RecurringPaymentId = "bill-home", OpeningPrincipal = 1000m,
            TrackingStartDate = new DateOnly(2026, 1, 1), AnnualRatePercent = 5m, TermPeriods = 12,
            InterestMethod = LoanInterestMethod.ReducingBalance, ScheduleFrequency = "Monthly", ScheduleDueDay = 1,
            ScheduleStartDate = new DateOnly(2026, 1, 1), ScheduleStatus = LoanScheduleStatus.Complete
        });
        await db.SaveChangesAsync();
        var provider = new ScriptedProvider()
            .Call("get_recurring", new { }, "c1")
            .Propose(new { type = "requestDeleteRecurring", payload = new { id = "bill-home" } })
            .Answer("That bill is linked to your Home loan, so it can't be deleted here.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("Delete my Home payment bill");

        Assert.Empty(outcome.Response.Actions);
    }

    [Theory]
    [InlineData("deleteEverything")]
    [InlineData("openSettings")]
    public async Task ActionsOutsideTheAllowedSetAreNeverReturned(string type)
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider().Propose(new { type, payload = new { } }).Answer("Done.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("open settings");

        Assert.Empty(outcome.Response.Actions);
    }

    [Fact]
    public async Task ARecurringToggleForAKnownBillIsAccepted()
    {
        await using var db = await SeedAsync();
        db.RecurringPayments.Add(new RecurringPayment
        {
            Id = "netflix", Name = "Netflix", Amount = 20, Frequency = "Monthly", Category = "Food",
            LedgerCategory = "Essentials", DueDate = 15, StartDate = "2026-01-15", Active = true
        });
        await db.SaveChangesAsync();
        // The always-on snapshot lists active bills, so the id is already known evidence.
        var provider = new ScriptedProvider()
            .Propose(new { type = "toggleRecurring", payload = new { id = "netflix", active = false } })
            .Answer("Netflix is now off.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("turn off my Netflix subscription");

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("toggleRecurring", action.Type);
        Assert.Equal("netflix", action.Payload["id"]?.ToString());
    }

    [Fact]
    public async Task AWishlistClaimNeedsAKnownUnclaimedItem()
    {
        await using var db = await SeedAsync();
        var rewards = Txn("rewards", new DateOnly(2026, 9, 26), "Rewards", 200m, "Rewards", "Rewards");
        db.Transactions.Add(rewards);
        db.WishlistItems.Add(new WishlistItem { Id = 7, Name = "Headphones", Price = 200, Priority = "Medium", IsActive = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var provider = new ScriptedProvider()
            .Call("get_goals_and_rewards", new { }, "c1")
            .Propose(new { type = "requestPurchaseWishlist", payload = new { id = "7" } })
            .Answer("Opening the claim confirmation.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("claim my Headphones wishlist item");

        Assert.Equal("requestPurchaseWishlist", Assert.Single(outcome.Response.Actions).Type);
    }

    [Fact]
    public async Task NavigationIsKeptAndProposalsAreCappedAtFourActions()
    {
        await using var db = await SeedAsync();
        var five = Enumerable.Repeat<object>(new { type = "openDashboard", payload = new { } }, 5).ToArray();
        var provider = new ScriptedProvider().Propose(five).Answer("Opening.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("open the dashboard");

        Assert.Equal(4, outcome.Response.Actions.Count);
        Assert.All(outcome.Response.Actions, action => Assert.Equal("openDashboard", action.Type));
    }

    [Theory]
    [InlineData("show me the ledger for NotACategory", "openLedger", """{"category":"NotACategory"}""")]
    [InlineData("How much did I spend this month?", "openLedger", """{"month":"Sep","year":2026}""")]
    [InlineData("Don't open the dashboard, just tell me my total", "openDashboard", "{}")]
    public async Task InvalidOrUnrequestedNavigationIsDropped(string message, string type, string payloadJson)
    {
        await using var db = await SeedAsync();
        var payload = System.Text.Json.JsonDocument.Parse(payloadJson).RootElement;
        var provider = new ScriptedProvider().Propose(new { type, payload }).Answer("Here you go.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync(message);

        Assert.Empty(outcome.Response.Actions);
    }

    [Fact]
    public async Task AHypotheticalQuestionNeverBecomesAnEdit()
    {
        await using var db = await SeedAsync();
        db.Transactions.Add(Txn("cafe", new DateOnly(2026, 9, 20), "Old Cafe", -18m, "Food"));
        await db.SaveChangesAsync();
        var provider = new ScriptedProvider()
            .Call("search_transactions", new { query = "Old Cafe" }, "c1")
            .Propose(new { type = "openEditLedgerDraft", payload = new { id = "cafe", changes = new { amount = 25 } } })
            .Answer("Hypothetically that would add 7 to your spending.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("What if I change Old Cafe to 25?");

        Assert.Empty(outcome.Response.Actions);
    }

    // ---------- drafts, placement, and wording ----------

    [Fact]
    public async Task ATransferDraftKeepsItsTwoDistinctBuckets()
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider()
            .Propose(Draft(new { description = "Move funds", amount = 100, txType = "transfer", ledgerCategorySpecified = true, transferSource = "Growth", transferTarget = "Stability" }))
            .Answer("Opening the transfer draft.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("transfer 100 from Growth to Stability");

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("Growth", action.Payload["transferSource"]?.ToString());
        Assert.Equal("Stability", action.Payload["transferTarget"]?.ToString());
    }

    [Fact]
    public async Task TwoMentionedAccountsInOneBucketStageAnInternalMoveNamedForBothSides()
    {
        await using var db = await SeedAsync(twoEssentialsAccounts: true);
        var provider = new ScriptedProvider()
            .Propose(Draft(new { amount = 50, txType = "transfer", ledgerCategorySpecified = true, transferSource = "Essentials", transferTarget = "Essentials", accountId = "acct-cimb", counterAccountId = "acct-ryt" }))
            .Answer("Preparing the move.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync(new AiChatRequest(
            "transfer 50 from @CIMB to @RYT", [],
            AccountMentions: [new AiAccountMention("CIMB", "acct-cimb"), new AiAccountMention("RYT", "acct-ryt")]));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("Essentials", action.Payload["transferTarget"]?.ToString());
        Assert.Equal("acct-cimb", action.Payload["accountId"]?.ToString());
        Assert.Equal("acct-ryt", action.Payload["counterAccountId"]?.ToString());
        Assert.Equal("Transfer CIMB to RYT", action.Payload["description"]?.ToString());
        Assert.Contains("mentionedAccounts", harness.FirstDeveloperMessage());
    }

    [Fact]
    public async Task MentionsPlaceTheAccountsEvenWhenTheModelOmitsThem()
    {
        await using var db = await SeedAsync(twoEssentialsAccounts: true);
        var provider = new ScriptedProvider()
            .Propose(Draft(new { description = "Move", amount = 50, txType = "transfer", ledgerCategorySpecified = true, transferSource = "Essentials", transferTarget = "Growth" }))
            .Answer("Preparing the move.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync(new AiChatRequest(
            "transfer 50 from @CIMB to @RYT", [],
            AccountMentions: [new AiAccountMention("CIMB", "acct-cimb"), new AiAccountMention("RYT", "acct-ryt")]));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("acct-cimb", action.Payload["accountId"]?.ToString());
        Assert.Equal("acct-ryt", action.Payload["counterAccountId"]?.ToString());
        Assert.Equal("Essentials", action.Payload["transferTarget"]?.ToString());
    }

    [Fact]
    public async Task AnAccountTheUserDoesNotOwnIsIgnoredRatherThanTrusted()
    {
        await using var db = await SeedAsync(twoEssentialsAccounts: true);
        var provider = new ScriptedProvider()
            .Propose(Draft(new { description = "Move", amount = 50, txType = "transfer", ledgerCategorySpecified = true, transferSource = "Essentials", transferTarget = "Growth" }))
            .Answer("Preparing the move.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync(new AiChatRequest(
            "transfer 50 to somewhere", [], AccountMentions: [new AiAccountMention("Someone Else", "acct-not-mine")]));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.False(action.Payload.TryGetValue("accountId", out var account) && account != null);
    }

    [Fact]
    public async Task AnAnswerToAClarificationCarriesTheOriginalRequest()
    {
        await using var db = await SeedAsync(twoEssentialsAccounts: true);
        var provider = new ScriptedProvider().Answer("Which date should I use?");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("yes, cimb to ryt", state: new AiConversationState(null, null, null, null) with
        {
            PendingLedgerRequest = "internal transfer from cimb to ryt RM50"
        });

        Assert.Contains("internal transfer from cimb to ryt RM50", harness.FirstDeveloperMessage());
        Assert.NotNull(outcome.Response.State);
    }

    [Fact]
    public async Task AClarifyingQuestionOnARecordRequestKeepsItForTheNextTurn()
    {
        await using var db = await SeedAsync(twoEssentialsAccounts: true);
        var provider = new ScriptedProvider().Answer("Which account is the 50 coming from?");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("transfer 50 from cimb to ryt");

        Assert.Equal("transfer 50 from cimb to ryt", outcome.Response.State?.PendingLedgerRequest);
    }

    [Fact]
    public async Task AnInflowDraftKeepsItsTypeAndAnOutflowDefaultsToEssentials()
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider()
            .Propose(
                Draft(new { description = "Cashback", amount = 25, txType = "inflow", category = "Food", ledgerCategory = "Income", ledgerCategorySpecified = false }),
                Draft(new { description = "Lunch", amount = 12, txType = "outflow", category = "Food", ledgerCategory = "Growth" }))
            .Answer("Staged.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("add a 25 cashback inflow and lunch 12");

        Assert.Equal("inflow", outcome.Response.Actions[0].Payload["txType"]?.ToString());
        Assert.Equal("Food", outcome.Response.Actions[0].Payload["category"]?.ToString());
        Assert.Equal("Essentials", outcome.Response.Actions[1].Payload["ledgerCategory"]?.ToString());
        Assert.False(Assert.IsType<bool>(outcome.Response.Actions[1].Payload["ledgerCategorySpecified"]));
    }

    [Fact]
    public async Task ASumTypedOnOneLineIsStillOneDraft()
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider()
            .Propose(Draft(new { description = "Mamak", amount = 20.30, txType = "outflow", category = "Food", ledgerCategory = "Essentials", ledgerCategorySpecified = false }))
            .Answer("Staging Mamak.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("And Mamak 18+2.30");

        Assert.Equal("Mamak", Assert.Single(outcome.Response.Actions).Payload["description"]?.ToString());
    }

    [Fact]
    public async Task FourShorthandRecordsAreAllStagedAndAnExplicitLineHintIsApplied()
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider()
            .Propose(
                Draft(new { description = "Nasi Lemak", amount = 12, txType = "outflow", category = "Food", ledgerCategory = "Essentials", ledgerCategorySpecified = false }),
                Draft(new { description = "Car Fuel", amount = 30, txType = "outflow", category = "Food", ledgerCategory = "Essentials", ledgerCategorySpecified = false }),
                Draft(new { description = "Kopi", amount = 3, txType = "outflow", category = "Food", ledgerCategory = "Essentials", ledgerCategorySpecified = false }),
                Draft(new { description = "Roti", amount = 2, txType = "outflow", category = "Food", ledgerCategory = "Essentials", ledgerCategorySpecified = false }))
            .Answer("Staged.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("Nasi Lemak 12\nCar Fuel 30 Transport Growth\nKopi 3\nRoti 2");

        Assert.Equal(4, outcome.Response.Actions.Count);
        Assert.Equal("Transport", outcome.Response.Actions[1].Payload["category"]?.ToString());
        Assert.Equal("Growth", outcome.Response.Actions[1].Payload["ledgerCategory"]?.ToString());
        Assert.StartsWith("I prepared 4 drafts", outcome.Response.Reply);
    }

    [Fact]
    public async Task AStagingClaimWithNothingStagedBecomesAnHonestReply()
    {
        await using var db = await SeedAsync();
        using var harness = new AgentHarness(db, new ScriptedProvider().Answer("Staging 1 draft: Mamak for MYR 20.30."));

        var outcome = await harness.AskAsync("add something for lunch");

        Assert.Empty(outcome.Response.Actions);
        Assert.Contains("Nothing was added", outcome.Response.Reply);
        Assert.False(outcome.Response.CloseChat);
    }

    [Fact]
    public async Task AClarifyingOfferKeepsItsOwnWording()
    {
        await using var db = await SeedAsync();
        using var harness = new AgentHarness(db, new ScriptedProvider().Answer("Do you want me to open a draft for that, or edit the existing one?"));

        var outcome = await harness.AskAsync("add something about lunch");

        Assert.Contains("Do you want me to open a draft", outcome.Response.Reply);
    }

    [Fact]
    public async Task SensitiveModeRejectsADraftAndTheReplySaysNothingWasAdded()
    {
        await using var db = await SeedAsync(hideSensitive: true);
        var provider = new ScriptedProvider()
            .Propose(Draft(new { description = "Lunch", amount = 20, category = "Food", txType = "outflow" }))
            .Answer("Staging a draft for lunch.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("add a food transaction for lunch");

        Assert.Empty(outcome.Response.Actions);
        Assert.Contains("Unhide balances", outcome.Response.Reply);
    }

    private static async Task<AppDbContext> SeedAsync(bool hideSensitive = false, bool twoEssentialsAccounts = false)
    {
        var db = TestHelpers.NewInMemoryContext();
        db.FinancialSettings.Add(new FinancialSetting { CycleDay = CycleDay, HideSensitive = hideSensitive, Currency = "MYR" });
        db.TransactionCategories.AddRange(
            new TransactionCategory { Id = "cat-food", Name = "Food", Type = CategoryFlowType.Both },
            new TransactionCategory { Id = "cat-transport", Name = "Transport", Type = CategoryFlowType.Both });
        if (twoEssentialsAccounts)
        {
            db.LedgerAccounts.AddRange(
                new LedgerAccount { Id = "acct-cimb", Name = "CIMB", Bucket = "Essentials", Kind = LedgerAccountKind.Bank },
                new LedgerAccount { Id = "acct-ryt", Name = "RYT", Bucket = "Essentials", Kind = LedgerAccountKind.Bank });
        }
        await db.SaveChangesAsync();
        return db;
    }
}
