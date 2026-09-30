using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using static FinancialAppApi.Tests.AiTools.AiToolTestSupport;

namespace FinancialAppApi.Tests.AiTools;

// "When was my last haircut?" -> "can you show me?" -> "redirect me to the ledger with the filter
// applied". The haircuts sit in earlier cycles (the latest on 23 Sep, cycle 2026-08), so every
// way of opening the ledger has to land where they are, and a navigation reply is not a draft.
public class AiLedgerNavigationTests
{
    private static object OpenLedger(object payload) => new { type = "openLedger", payload };

    [Fact]
    public async Task AFilterWithoutAPeriodSearchesAllHistoryAndTheReplyIsKept()
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider()
            .Propose(OpenLedger(new { search = "haircut" }))
            .Answer("I opened the ledger filtered to haircut across all your history.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("redirect me to ledger page with the filter applied");

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("openLedger", action.Type);
        Assert.Equal("haircut", action.Payload["search"]?.ToString());
        Assert.Equal(true, action.Payload["allCycles"]);
        Assert.Equal("I opened the ledger filtered to haircut across all your history.", outcome.Response.Reply);
    }

    [Fact]
    public async Task ShowMeOpensThePreviousTurnsTransactionInItsOwnCycle()
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider()
            .Propose(OpenLedger(new { id = "cut-latest", search = "haircut", month = "Oct", year = 2026 }))
            .Answer("Here it is: your haircut on 23 Sep 2026, opened in the ledger.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("can you show me?", state: new AiConversationState(["cut-latest"]));

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("cut-latest", action.Payload["id"]?.ToString());
        // 23 Sep falls before the 25th, so it belongs to the cycle that started on 25 Aug.
        Assert.Equal("Aug", action.Payload["month"]?.ToString());
        Assert.Equal(2026, action.Payload["year"]);
        Assert.False(action.Payload.ContainsKey("search"));
        Assert.Contains("opened in the ledger", outcome.Response.Reply);
    }

    [Fact]
    public async Task ACycleKeyBecomesTheMonthAndYearTheLedgerReads()
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider()
            .Propose(OpenLedger(new { cycleKey = "2026-07", search = "haircut", allCycles = true }))
            .Answer("Opened July's cycle.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("open my haircuts in the 2026-07 cycle");

        var action = Assert.Single(outcome.Response.Actions);
        Assert.Equal("Jul", action.Payload["month"]?.ToString());
        Assert.Equal(2026, action.Payload["year"]);
        Assert.False(action.Payload.ContainsKey("allCycles"));
        Assert.False(action.Payload.ContainsKey("cycleKey"));
    }

    [Fact]
    public async Task AnIdNoToolReturnedIsRejectedBackToTheModel()
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider()
            .Propose(OpenLedger(new { id = "cut-latest" }))
            .Answer("I could not open that one.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("show me the haircut");

        Assert.Empty(outcome.Response.Actions);
        var verdict = provider.OutputFor("call_0").GetProperty("results")[0];
        Assert.Equal("rejected", verdict.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ABillIdNoToolReturnedCannotBeHighlighted()
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider()
            .Propose(new { type = "openRecurring", payload = new { id = "rp-unknown" } })
            .Answer("Could not open it.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("open my netflix bill");

        Assert.Empty(outcome.Response.Actions);
    }

    [Fact]
    public async Task AHalfPeriodIsRejectedRatherThanSilentlyShowingTheCurrentCycle()
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider()
            .Propose(OpenLedger(new { month = "Aug", search = "haircut" }))
            .Answer("Could not open it.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("show my august haircuts");

        Assert.Empty(outcome.Response.Actions);
        Assert.Contains("cycleKey", provider.OutputFor("call_0").GetProperty("results")[0].GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("can you show me?")]
    [InlineData("could you open it?")]
    [InlineData("can you take me there?")]
    public async Task ARequestPhrasedAsAQuestionMayStillOpenTheLedger(string message)
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider()
            .Propose(OpenLedger(new { search = "haircut" }))
            .Answer("Opened.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync(message);

        Assert.Single(outcome.Response.Actions);
    }

    [Fact]
    public async Task APlainQuestionStillCannotOpenTheLedger()
    {
        await using var db = await SeedAsync();
        var provider = new ScriptedProvider()
            .Propose(OpenLedger(new { search = "haircut" }))
            .Answer("Your last haircut was on 23 Sep 2026.");
        using var harness = new AgentHarness(db, provider);

        var outcome = await harness.AskAsync("when is my last haircut?");

        Assert.Empty(outcome.Response.Actions);
    }

    [Fact]
    public async Task AnAnswerThatMentionsAddedRecordsIsNotRewrittenForAQuestion()
    {
        await using var db = await SeedAsync();
        using var harness = new AgentHarness(db, new ScriptedProvider()
            .Answer("You added 4 haircut transactions between 23 Jun and 23 Sep 2026."));

        var outcome = await harness.AskAsync("how many haircuts have I had");

        Assert.StartsWith("You added 4 haircut transactions", outcome.Response.Reply);
    }

    private static async Task<AppDbContext> SeedAsync()
    {
        var db = TestHelpers.NewInMemoryContext();
        db.FinancialSettings.Add(new FinancialSetting { CycleDay = CycleDay, HideSensitive = false, Currency = "MYR" });
        db.TransactionCategories.Add(new TransactionCategory { Id = "cat-care", Name = "Personal Care", Type = CategoryFlowType.Both });
        db.Transactions.AddRange(
            Txn("cut-jun", new DateOnly(2026, 6, 23), "Haircut", -22m),
            Txn("cut-latest", new DateOnly(2026, 9, 23), "Haircut", -22m));
        await db.SaveChangesAsync();
        return db;
    }
}
