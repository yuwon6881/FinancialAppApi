using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;
using FinancialAppApi.Services.AI;
using FinancialAppApi.Services.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using static FinancialAppApi.Tests.AiTools.AiToolTestSupport;

namespace FinancialAppApi.Tests.AiTools;

public class CoreToolsTests
{
    // Distinctive amounts: if any of these strings reaches a sensitive-mode result, money leaked.
    private static readonly string[] SeededAmounts = ["5123.45", "137.29", "41.37", "43.71", "999.13"];

    [Fact]
    public async Task CycleSummary_ReportsCashFlowBucketNetAndProgressForTheCurrentCycle()
    {
        await using var db = await SeedCycleAsync();

        var data = (await RunToolAsync(db, "get_cycle_summary")).GetProperty("data");

        Assert.Equal("2026-09", data.GetProperty("cycle").GetString());
        Assert.Equal("InProgress", data.GetProperty("phase").GetString());
        Assert.Equal("2026-09-29", data.GetProperty("observedThrough").GetString());
        Assert.Equal(5123.45m, data.GetProperty("income").GetDecimal());
        Assert.Equal(5143.45m, data.GetProperty("inflow").GetDecimal());
        Assert.Equal(20m, data.GetProperty("otherInflow").GetDecimal());
        // Groceries + haircut + Netflix; the transfer is not spending and the old cycle is excluded.
        Assert.Equal(222.37m, data.GetProperty("outflow").GetDecimal());
        Assert.Equal("Food", data.GetProperty("categorySpend")[0].GetProperty("name").GetString());
        var ledgerNet = data.GetProperty("ledgerNet").EnumerateArray()
            .ToDictionary(e => e.GetProperty("ledgerCategory").GetString()!, e => e.GetProperty("net").GetDecimal());
        Assert.Equal(-137.29m - 41.37m + 20m - 43.71m - 300m, ledgerNet["Essentials"]);
        Assert.Equal(300m, ledgerNet["Growth"]);

        var insights = data.GetProperty("insights");
        Assert.Equal(30, insights.GetProperty("cycleLengthDays").GetInt32());
        Assert.Equal(5, insights.GetProperty("elapsedDays").GetInt32());
        Assert.Equal(2, insights.GetProperty("noSpendDaysObserved").GetInt32());
        Assert.Equal(43.71m, insights.GetProperty("committedSpend").GetDecimal());
        Assert.Equal("groceries", insights.GetProperty("largestExpense").GetProperty("id").GetString());
    }

    [Fact]
    public async Task CompareCycles_ComputesChangesServerSideAndFlagsThePartialCycle()
    {
        await using var db = await SeedCycleAsync();

        var data = (await RunToolAsync(db, "compare_cycles", """{"lastN":2}""")).GetProperty("data");

        var cycles = data.GetProperty("cycles");
        Assert.Equal("2026-08", cycles[0].GetProperty("cycle").GetString());
        Assert.Equal(999.13m, cycles[0].GetProperty("outflow").GetDecimal());
        Assert.False(cycles[0].TryGetProperty("outflowChange", out _));
        Assert.Equal(222.37m - 999.13m, cycles[1].GetProperty("outflowChange").GetDecimal());
        Assert.Contains("InProgress", data.GetProperty("note").GetString());
    }

    [Fact]
    public async Task SpendingBreakdown_MergesMerchantSpellingsAndReportsShares()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        db.Transactions.AddRange(
            Txn("g1", new DateOnly(2026, 9, 26), "GRAB ride", -10m, "Transport"),
            Txn("g2", new DateOnly(2026, 9, 27), "Grab ride.", -20m, "Transport"),
            Txn("g3", new DateOnly(2026, 9, 28), "grab  RIDE", -30m, "Transport"),
            Txn("coffee", new DateOnly(2026, 9, 28), "Coffee", -40m, "Food"));
        await db.SaveChangesAsync();

        var data = (await RunToolAsync(db, "get_spending_breakdown", """{"groupBy":"merchant"}""")).GetProperty("data");

        var first = data.GetProperty("groups")[0];
        Assert.Equal("grab ride", first.GetProperty("key").GetString());
        Assert.Equal(3, first.GetProperty("count").GetInt32());
        Assert.Equal(60m, first.GetProperty("amount").GetDecimal());
        Assert.Equal(60m, first.GetProperty("share").GetDecimal());
        Assert.Equal(100m, data.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task PurchasePattern_UsesAllHistoryAndIgnoresSystemGeneratedRows()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        var generated = Txn("system", new DateOnly(2026, 6, 1), "Haircut", -40m);
        generated.ExcludeFromAutocomplete = true;
        db.Transactions.AddRange(
            Txn("c1", new DateOnly(2026, 3, 1), "Haircut", -40m),
            Txn("c2", new DateOnly(2026, 4, 1), "Haircut", -40m),
            Txn("c3", new DateOnly(2026, 5, 1), "Haircut", -40m),
            generated);
        await db.SaveChangesAsync();

        var data = (await RunToolAsync(db, "get_purchase_pattern", """{"query":"haircut"}""")).GetProperty("data");

        var metric = data.GetProperty("metric");
        Assert.Equal(3, metric.GetProperty("transactionCount").GetInt32());
        Assert.Equal("2026-05-01", metric.GetProperty("lastPurchaseDate").GetString());
        Assert.Equal("c3", data.GetProperty("latest")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Accounts_ReportServiceBalancesAndDerivedBucketTotals()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        TestHelpers.SeedLedgerAccounts(db);
        var spend = Txn("spend", new DateOnly(2026, 9, 26), "Groceries", -120m, "Food");
        spend.AccountId = TestHelpers.AccountIdFor("Essentials");
        db.Transactions.Add(spend);
        await db.SaveChangesAsync();
        var service = NewAccountService(db);
        var expected = await service.GetBalancesAsync(await service.GetAccountsAsync());

        var (result, context) = await RunWithContextAsync(db, "get_accounts");

        var data = result.GetProperty("data");
        var essentials = data.GetProperty("accounts").EnumerateArray()
            .Single(account => account.GetProperty("id").GetString() == TestHelpers.AccountIdFor("Essentials"));
        Assert.Equal(expected[TestHelpers.AccountIdFor("Essentials")], essentials.GetProperty("balance").GetDecimal());
        var bucket = data.GetProperty("buckets").EnumerateArray().Single(b => b.GetProperty("bucket").GetString() == "Essentials");
        Assert.Equal(expected[TestHelpers.AccountIdFor("Essentials")], bucket.GetProperty("balance").GetDecimal());
        Assert.True(context.Evidence.Contains(AiEvidenceLedger.Account, TestHelpers.AccountIdFor("Growth")));
    }

    [Fact]
    public async Task Recurring_DerivesNextDueDateFromTheScheduleAndNormalisesCost()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        db.RecurringPayments.AddRange(
            new RecurringPayment { Id = "spotify", Name = "Spotify", Amount = 15, Frequency = "Monthly", Category = "Entertainment", LedgerCategory = "Rewards", NextDueDate = "2026-01-20", DueDate = 20, StartDate = "2026-01-01", Active = true },
            new RecurringPayment { Id = "domain", Name = "Domain Renewal", Amount = 40, Frequency = "Annually", Category = "Software", LedgerCategory = "Essentials", NextDueDate = "2026-11-01", DueDate = 1, StartDate = "2026-01-01", Active = true },
            new RecurringPayment { Id = "gym", Name = "Gym", Amount = 99, Frequency = "Monthly", Category = "Health", LedgerCategory = "Essentials", DueDate = 5, StartDate = "2026-01-01", Active = false });
        await db.SaveChangesAsync();

        var (result, context) = await RunWithContextAsync(db, "get_recurring", """{"status":"active"}""");

        var data = result.GetProperty("data");
        var spotify = data.GetProperty("payments").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "spotify");
        // Never the stale stored column (2026-01-20); the next pending occurrence after today.
        Assert.Equal("2026-10-20", spotify.GetProperty("nextDueDate").GetString());
        Assert.DoesNotContain(data.GetProperty("payments").EnumerateArray(), p => p.GetProperty("id").GetString() == "gym");
        Assert.Equal(18.33m, data.GetProperty("costSummary").GetProperty("monthlyTotal").GetDecimal());
        Assert.True(context.Evidence.Contains(AiEvidenceLedger.Recurring, "spotify"));
        Assert.False(context.Evidence.Contains(AiEvidenceLedger.Recurring, "gym"));
    }

    [Fact]
    public async Task EveryTool_LeaksNoSeededAmountInSensitiveMode()
    {
        await using var db = await SeedCycleAsync();
        db.RecurringPayments.Add(new RecurringPayment { Id = "netflix", Name = "Netflix", Amount = 43.71m, Frequency = "Monthly", Category = "Entertainment", LedgerCategory = "Essentials", DueDate = 28, StartDate = "2026-01-01", Active = true });
        TestHelpers.SeedLedgerAccounts(db);
        await db.SaveChangesAsync();
        var executor = NewExecutor(AllTools(db));
        var calls = new (string Tool, string Args)[]
        {
            ("search_transactions", """{"query":"haircut"}"""),
            ("search_transactions", "{}"),
            ("get_cycle_summary", "{}"),
            ("compare_cycles", """{"lastN":2}"""),
            ("get_spending_breakdown", "{}"),
            ("get_spending_breakdown", """{"groupBy":"merchant","txType":"inflow"}"""),
            ("get_purchase_pattern", """{"query":"haircut"}"""),
            ("get_accounts", "{}"),
            ("get_recurring", "{}")
        };

        foreach (var (tool, args) in calls)
        {
            var execution = await RunAsync(executor, NewContext(sensitive: true), tool, args);
            Assert.True(execution.Succeeded, $"{tool} failed: {execution.Output}");
            foreach (var amount in SeededAmounts)
                Assert.DoesNotContain(amount, execution.Output);
        }
    }

    // Current cycle 2026-09 runs Sep 25 - Oct 24; today is Sep 29.
    private static async Task<AppDbContext> SeedCycleAsync()
    {
        var db = TestHelpers.NewInMemoryContext();
        var netflix = Txn("netflix-bill", new DateOnly(2026, 9, 28), "Netflix", -43.71m, "Entertainment");
        netflix.RecurringPaymentId = "netflix";
        db.Transactions.AddRange(
            Txn("salary", new DateOnly(2026, 9, 25), "Salary", 5123.45m, "Salary", "Income"),
            Txn("groceries", new DateOnly(2026, 9, 26), "Groceries", -137.29m, "Food"),
            Txn("haircut", new DateOnly(2026, 9, 27), "Haircut", -41.37m),
            Txn("to-growth", new DateOnly(2026, 9, 27), "Move to Growth", 300m, "Transfer", "Transfer:Essentials->Growth"),
            Txn("refund", new DateOnly(2026, 9, 28), "Grocery refund", 20m, "Food"),
            netflix,
            Txn("august", new DateOnly(2026, 8, 30), "Laptop", -999.13m, "Electronics"));
        await db.SaveChangesAsync();
        return db;
    }

    private static IAiTool[] AllTools(AppDbContext db)
    {
        var transactions = new AiTransactionQueryService(db);
        var accounts = NewAccountService(db);
        var occurrences = new RecurringOccurrenceLedgerService(
            db, new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance), FinancialClock.Utc);
        return
        [
            new SearchTransactionsTool(transactions),
            new GetCycleSummaryTool(transactions),
            new CompareCyclesTool(transactions),
            new GetSpendingBreakdownTool(transactions, accounts),
            new GetPurchasePatternTool(transactions),
            new GetAccountsTool(accounts),
            new GetRecurringTool(db, occurrences)
        ];
    }

    private static LedgerAccountService NewAccountService(AppDbContext db) => new(
        db,
        new LedgerAccountBalanceService(db, new CycleBalanceService(db), FinancialClock.Utc),
        new CycleBalanceService(db),
        FinancialClock.Utc);

    private static async Task<JsonElement> RunToolAsync(AppDbContext db, string tool, string args = "{}") =>
        (await RunWithContextAsync(db, tool, args)).Result;

    private static async Task<(JsonElement Result, AiToolContext Context)> RunWithContextAsync(AppDbContext db, string tool, string args = "{}")
    {
        var context = NewContext();
        var execution = await RunAsync(NewExecutor(AllTools(db)), context, tool, args);
        Assert.True(execution.Succeeded, execution.Output);
        return (Parse(execution), context);
    }
}
