using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Services.AI;
using FinancialAppApi.Services.AI.Tools;
using static FinancialAppApi.Tests.AiTools.AiToolTestSupport;

namespace FinancialAppApi.Tests.AiTools;

public class SearchTransactionsToolTests
{
    [Fact]
    public async Task LatestHaircut_IsFoundInOlderHistoryWhenTheCurrentCycleHasNone()
    {
        // The reported failure: the old router scoped a question with no cycle words to the current
        // cycle, found no haircut there, and told the user it had no information.
        await using var db = TestHelpers.NewInMemoryContext();
        db.Transactions.AddRange(
            Txn("cut-old", new DateOnly(2025, 11, 3), "Haircut", -35m),
            Txn("cut-latest", new DateOnly(2026, 2, 14), "Haircut at Barber King", -40m),
            Txn("groceries", new DateOnly(2026, 9, 27), "Groceries", -120m, "Food"));
        await db.SaveChangesAsync();

        var (result, context) = await SearchAsync(db, """{"query":"haircut","sort":"newest","limit":1}""");

        var data = result.GetProperty("data");
        Assert.True(data.GetProperty("scope").GetProperty("allHistory").GetBoolean());
        Assert.Equal(2, data.GetProperty("totalMatches").GetInt32());
        Assert.Equal("2026-02-14", data.GetProperty("lastDate").GetString());
        var row = Assert.Single(data.GetProperty("rows").EnumerateArray());
        Assert.Equal("cut-latest", row.GetProperty("id").GetString());
        Assert.Equal("2026-02-14", row.GetProperty("date").GetString());
        Assert.Equal("2026-01", row.GetProperty("cycle").GetString());
        Assert.Equal(75m, data.GetProperty("totalOutflow").GetDecimal());
        Assert.True(context.Evidence.Contains(AiEvidenceLedger.Transaction, "cut-latest"));
        Assert.False(context.Evidence.Contains(AiEvidenceLedger.Transaction, "cut-old"));
    }

    [Fact]
    public async Task SpacingLadder_MatchesHairCutAgainstHaircutAndNamesWhatMatched()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        db.Transactions.Add(Txn("cut", new DateOnly(2026, 2, 14), "Haircut", -40m));
        await db.SaveChangesAsync();

        var (result, _) = await SearchAsync(db, """{"query":"hair cut"}""");

        var data = result.GetProperty("data");
        Assert.Equal("spacing", data.GetProperty("matchMode").GetString());
        Assert.Equal(1, data.GetProperty("totalMatches").GetInt32());
        Assert.Equal("Haircut", data.GetProperty("sampleDescriptions")[0].GetString());
    }

    [Fact]
    public async Task NoMatchInAPopulatedCycle_IsDistinctFromAnEmptyCycle()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        db.Transactions.AddRange(
            Txn("cut", new DateOnly(2026, 2, 14), "Haircut", -40m),
            Txn("groceries", new DateOnly(2026, 9, 27), "Groceries", -120m, "Food"));
        await db.SaveChangesAsync();

        var (inCurrent, _) = await SearchAsync(db, """{"query":"haircut","cycleKeys":["current"]}""");
        var (inEmpty, _) = await SearchAsync(db, """{"query":"haircut","cycleKeys":["2024-03"]}""");

        Assert.Equal(0, inCurrent.GetProperty("data").GetProperty("totalMatches").GetInt32());
        Assert.Equal("none", inCurrent.GetProperty("data").GetProperty("matchMode").GetString());
        Assert.True(inCurrent.GetProperty("data").GetProperty("scopeHasTransactions").GetBoolean());
        Assert.False(inEmpty.GetProperty("data").GetProperty("scopeHasTransactions").GetBoolean());
        Assert.False(inEmpty.GetProperty("data").TryGetProperty("totalOutflow", out _));
    }

    [Fact]
    public async Task Totals_FollowReportSemanticsAndCoverEveryMatchBeyondTheRowLimit()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        db.Transactions.AddRange(
            Txn("grab-1", new DateOnly(2026, 9, 1), "Grab ride", -12m, "Transport"),
            Txn("grab-2", new DateOnly(2026, 9, 2), "Grab ride", -18m, "Transport"),
            Txn("grab-refund", new DateOnly(2026, 9, 3), "Grab refund", 5m, "Transport"),
            Txn("grab-move", new DateOnly(2026, 9, 4), "Grab wallet top up", -100m, "Transfer", "Transfer:Essentials"),
            Txn("grab-gone", new DateOnly(2026, 9, 5), "Grab ride", -50m, "Transport", "Discarded"));
        await db.SaveChangesAsync();

        var (result, _) = await SearchAsync(db, """{"query":"grab","limit":1}""");

        var data = result.GetProperty("data");
        // Discarded markers are invisible; the transfer is listed but never counted as spending.
        Assert.Equal(4, data.GetProperty("totalMatches").GetInt32());
        Assert.Equal(1, data.GetProperty("returned").GetInt32());
        Assert.Equal(30m, data.GetProperty("totalOutflow").GetDecimal());
        Assert.Equal(5m, data.GetProperty("totalInflow").GetDecimal());
    }

    [Fact]
    public async Task Filters_NarrowByDatesTypeAndMagnitudeAndSortBySize()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        db.Transactions.AddRange(
            Txn("small", new DateOnly(2026, 8, 1), "Coffee", -5m, "Food"),
            Txn("big", new DateOnly(2026, 8, 2), "Dinner", -250m, "Food"),
            Txn("mid", new DateOnly(2026, 8, 3), "Lunch", -40m, "Food"),
            Txn("salary", new DateOnly(2026, 8, 4), "Salary", 5000m, "Salary", "Income"),
            Txn("early", new DateOnly(2026, 6, 1), "Feast", -900m, "Food"));
        await db.SaveChangesAsync();

        var (result, _) = await SearchAsync(db,
            """{"startDate":"2026-08-01","endDate":"2026-08-31","txType":"outflow","minAmount":10,"sort":"largest"}""");

        var ids = result.GetProperty("data").GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("id").GetString()).ToList();
        Assert.Equal(["big", "mid"], ids);
    }

    [Fact]
    public async Task OtherUsersTransactionsAreNeverReturned()
    {
        var store = SharedStore();
        await using (var other = store("other-user"))
        {
            var theirs = Txn("theirs", new DateOnly(2026, 3, 1), "Haircut", -99m);
            theirs.UserId = "other-user";
            other.Transactions.Add(theirs);
            await other.SaveChangesAsync();
        }
        await using var mine = store(TestHelpers.DefaultUserId);
        mine.Transactions.Add(Txn("mine", new DateOnly(2026, 2, 1), "Haircut", -40m));
        await mine.SaveChangesAsync();

        var (result, _) = await SearchAsync(mine, """{"query":"haircut"}""");

        var ids = result.GetProperty("data").GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("id").GetString()).ToList();
        Assert.Equal(["mine"], ids);
    }

    [Fact]
    public async Task SensitiveMode_HidesAmountsAndRefusesAmountRevealingArguments()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        db.Transactions.Add(Txn("cut", new DateOnly(2026, 2, 14), "Haircut", -40m));
        await db.SaveChangesAsync();

        var (result, _) = await SearchAsync(db, """{"query":"haircut"}""", sensitive: true);
        var (thresholded, _) = await SearchAsync(db, """{"query":"haircut","minAmount":10}""", sensitive: true);

        var data = result.GetProperty("data");
        Assert.Equal("2026-02-14", data.GetProperty("lastDate").GetString());
        Assert.False(data.TryGetProperty("totalOutflow", out _));
        Assert.False(data.GetProperty("rows")[0].TryGetProperty("amount", out _));
        Assert.DoesNotContain("40", data.GetRawText());
        Assert.False(thresholded.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("""{"cycleKeys":["current"],"startDate":"2026-01-01"}""", "either")]
    [InlineData("""{"startDate":"2026-05-01","endDate":"2026-04-01"}""", "before")]
    [InlineData("""{"cycleKeys":["someday"]}""", "2026-09")]
    [InlineData("""{"sort":"random"}""", "newest")]
    public async Task InvalidArguments_ReturnCorrectableErrors(string arguments, string hint)
    {
        await using var db = TestHelpers.NewInMemoryContext();

        var (result, _) = await SearchAsync(db, arguments);

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains(hint, result.GetProperty("error").GetString());
    }

    private static async Task<(JsonElement Result, AiToolContext Context)> SearchAsync(
        AppDbContext db,
        string arguments,
        bool sensitive = false)
    {
        var executor = NewExecutor(new SearchTransactionsTool(new AiTransactionQueryService(db)));
        var context = NewContext(sensitive);
        var execution = await RunAsync(executor, context, "search_transactions", arguments);
        return (Parse(execution), context);
    }
}
