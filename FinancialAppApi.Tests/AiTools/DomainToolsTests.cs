using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;
using FinancialAppApi.Services.AI;
using FinancialAppApi.Services.AI.Tools;
using FinancialAppApi.Services.Loans;
using FinancialAppApi.Services.SavingsGoals;
using FinancialAppApi.Services.Stability;
using FinancialAppApi.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static FinancialAppApi.Tests.AiTools.AiToolTestSupport;

namespace FinancialAppApi.Tests.AiTools;

public class DomainToolsTests
{
    [Fact]
    public async Task Analyze_FlagsDuplicatesAndInScopeAnomaliesOnlyAgainstPriorHistory()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        // Four ordinary Food spends in earlier cycles form the baseline; one earlier spike is out of scope.
        db.Transactions.AddRange(
            Txn("f1", new DateOnly(2026, 6, 1), "Lunch", -20m, "Food"),
            Txn("f2", new DateOnly(2026, 7, 1), "Lunch", -22m, "Food"),
            Txn("f3", new DateOnly(2026, 8, 1), "Lunch", -18m, "Food"),
            Txn("f4", new DateOnly(2026, 8, 20), "Lunch", -21m, "Food"),
            Txn("old-spike", new DateOnly(2026, 7, 10), "Banquet", -380m, "Food"),
            Txn("spike", new DateOnly(2026, 9, 26), "Wedding dinner", -400m, "Food"),
            Txn("dup-a", new DateOnly(2026, 9, 27), "Netflix", -15.99m, "Entertainment"),
            Txn("dup-b", new DateOnly(2026, 9, 28), "Netflix", -15.99m, "Entertainment"));
        await db.SaveChangesAsync();
        var executor = NewExecutor(new AnalyzeTransactionsTool(new AiTransactionQueryService(db)));
        var context = NewContext();

        var anomalies = Parse(await RunAsync(executor, context, "analyze_transactions", """{"kind":"anomalies"}""")).GetProperty("data");
        var duplicates = Parse(await RunAsync(executor, context, "analyze_transactions", """{"kind":"duplicates"}""")).GetProperty("data");

        var anomalyIds = anomalies.GetProperty("findings").EnumerateArray().SelectMany(f => f.GetProperty("ids").EnumerateArray()).Select(id => id.GetString()).ToList();
        Assert.Contains("spike", anomalyIds);
        Assert.DoesNotContain("old-spike", anomalyIds);
        var pair = duplicates.GetProperty("findings")[0].GetProperty("ids").EnumerateArray().Select(id => id.GetString()).ToList();
        Assert.Equal(["dup-a", "dup-b"], pair.Order());
        Assert.True(context.Evidence.Contains(AiEvidenceLedger.Transaction, "dup-b"));
    }

    [Fact]
    public async Task CategoryLimits_ProjectAnUnfinishedCycleFromItsPaceSoFar()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        db.CategorySpendingGuides.Add(new CategorySpendingGuide { Id = "g", CategoryName = "Food", LimitAmount = 300m, EffectiveFromCycleKey = "2026-01" });
        db.TransactionCategories.Add(new TransactionCategory { Name = "Food", Type = CategoryFlowType.Both });
        db.Transactions.Add(Txn("food", new DateOnly(2026, 9, 26), "Groceries", -250m, "Food"));
        await db.SaveChangesAsync();
        var tool = new GetCategoryLimitsTool(db, new AiTransactionQueryService(db), NewBillStatuses(db));

        var data = Parse(await RunAsync(NewExecutor(tool), NewContext(), "get_category_limits")).GetProperty("data");

        var food = data.GetProperty("limits")[0];
        Assert.Equal("2026-09", data.GetProperty("cycle").GetString());
        Assert.Equal(250m, food.GetProperty("spent").GetDecimal());
        Assert.Equal(50m, food.GetProperty("remaining").GetDecimal());
        // 250 over 5 elapsed days stretched across a 30-day cycle projects 1500: over the limit, not yet exceeded.
        Assert.Equal(1500m, food.GetProperty("projectedSpend").GetDecimal());
        Assert.Equal("Watch", food.GetProperty("status").GetString());
    }

    [Fact]
    public async Task BudgetPlan_ReportsAllocationAndStabilityFundAgainstTarget()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        db.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = CycleDay, HideSensitive = false, TargetStabilityFund = 1000m,
            EssentialsAlloc = 0.5m, GrowthAlloc = 0.2m, StabilityAlloc = 0.2m, RewardsAlloc = 0.1m
        });
        db.Transactions.Add(Txn("fund", new DateOnly(2026, 9, 26), "Top up", 400m, "Savings", "Stability"));
        await db.SaveChangesAsync();
        var tool = new GetBudgetPlanTool(db, new StabilityRecoveryService(db, new CycleBalanceService(db)));
        var executor = NewExecutor(tool);

        var data = Parse(await RunAsync(executor, NewContext(), "get_budget_plan")).GetProperty("data");
        var hidden = Parse(await RunAsync(NewExecutor(tool), NewContext(sensitive: true), "get_budget_plan")).GetProperty("data");

        Assert.Equal(0.2m, data.GetProperty("allocation").GetProperty("growth").GetDecimal());
        var fund = data.GetProperty("stabilityFund");
        Assert.Equal(400m, fund.GetProperty("currentBalance").GetDecimal());
        Assert.Equal(40m, fund.GetProperty("percentReached").GetDecimal());
        Assert.Equal(600m, fund.GetProperty("stillNeeded").GetDecimal());
        Assert.False(hidden.TryGetProperty("allocation", out _));
        Assert.False(hidden.TryGetProperty("stabilityFund", out _));
    }

    [Fact]
    public async Task LedgerForecast_ProjectsFromTheBucketBalanceAndActiveCycleAverage()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        TestHelpers.SeedLedgerAccounts(db);
        foreach (var (id, date) in new[] { ("g6", new DateOnly(2026, 6, 26)), ("g7", new DateOnly(2026, 7, 26)), ("g8", new DateOnly(2026, 8, 26)) })
        {
            var deposit = Txn(id, date, "Invest", 200m, "Savings", "Growth");
            deposit.AccountId = TestHelpers.AccountIdFor("Growth");
            db.Transactions.Add(deposit);
        }
        await db.SaveChangesAsync();
        var accounts = NewAccountService(db);
        var balance = (await accounts.GetBalancesAsync(await accounts.GetAccountsAsync()))[TestHelpers.AccountIdFor("Growth")];
        var tool = new ForecastLedgerBalanceTool(new AiTransactionQueryService(db), accounts);

        var data = Parse(await RunAsync(NewExecutor(tool), NewContext(), "forecast_ledger_balance",
            $$"""{"ledgerCategory":"Growth","targetAmount":{{balance + 1000m}}}""")).GetProperty("data");
        var hidden = Parse(await RunAsync(NewExecutor(tool), NewContext(sensitive: true), "forecast_ledger_balance",
            """{"ledgerCategory":"Growth","targetAmount":5000}"""));

        Assert.Equal(balance, data.GetProperty("currentBalance").GetDecimal());
        // Three of the six rate cycles had activity; empty cycles are skipped, never averaged as zero.
        Assert.Equal(200m, data.GetProperty("savingsPerCycle").GetDecimal());
        Assert.Equal(5, data.GetProperty("estimatedCycles").GetInt32());
        Assert.Equal("estimated-from-completed-cycles", data.GetProperty("status").GetString());
        Assert.False(hidden.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task GoalsAndRewards_ListsWishlistAffordabilityAndHidesItInSensitiveMode()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        db.WishlistItems.Add(new WishlistItem { Name = "Camera", Price = 537.19m, Priority = "High", IsActive = true, CreatedAt = new DateTime(2026, 1, 1) });
        await db.SaveChangesAsync();
        var tool = new GetGoalsAndRewardsTool(db, NewGoalService(db), new AiTransactionQueryService(db));

        var visible = Parse(await RunAsync(NewExecutor(tool), NewContext(), "get_goals_and_rewards")).GetProperty("data");
        var hidden = await RunAsync(NewExecutor(tool), NewContext(sensitive: true), "get_goals_and_rewards");

        var camera = visible.GetProperty("rewards")[0];
        Assert.Equal("Camera", camera.GetProperty("name").GetString());
        Assert.False(camera.GetProperty("claimable").GetBoolean());
        Assert.Equal("insufficient-history", visible.GetProperty("forecast")[0].GetProperty("status").GetString());
        Assert.DoesNotContain("537.19", hidden.Output);
        Assert.DoesNotContain("claimable", hidden.Output);
        Assert.Contains("Camera", hidden.Output);
    }

    [Fact]
    public async Task Loans_WithNoLoansAnswerEmptyAndRejectAnUnknownId()
    {
        await using var db = TestHelpers.NewInMemoryContext();
        var executor = NewExecutor(new GetLoansTool(new LoanService(db)));

        var all = Parse(await RunAsync(executor, NewContext(), "get_loans")).GetProperty("data");
        var unknown = Parse(await RunAsync(executor, NewContext(), "get_loans", """{"loanId":"nope"}"""));

        Assert.Equal(0, all.GetProperty("totalLoans").GetInt32());
        Assert.False(unknown.GetProperty("ok").GetBoolean());
        Assert.Contains("get_loans", unknown.GetProperty("error").GetString());
    }

    [Fact]
    public async Task EveryToolIsRegisteredInTheRealHostAndInvestmentsStayHiddenInSensitiveMode()
    {
        using var factory = new FinancialApiFactory();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().SetCurrentUser(TestHelpers.DefaultUserId);
        var registry = scope.ServiceProvider.GetRequiredService<AiToolRegistry>();
        var executor = scope.ServiceProvider.GetRequiredService<AiToolExecutor>();

        Assert.Equal(
            [
                "analyze_transactions", "compare_cycles", "forecast_ledger_balance", "get_accounts", "get_budget_plan",
                "get_category_limits", "get_cycle_summary", "get_goals_and_rewards", "get_investments", "get_loans",
                "get_purchase_pattern", "get_recurring", "get_spending_breakdown", "search_transactions"
            ],
            registry.Definitions.Select(definition => definition.Name));
        var hidden = Parse(await RunAsync(executor, NewContext(sensitive: true), "get_investments"));
        Assert.Contains("sensitive mode", hidden.GetProperty("error").GetString());
    }

    private static AiRecurringBillStatusService NewBillStatuses(AppDbContext db) => new(
        db,
        new RecurringOccurrenceLedgerService(db, new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance), FinancialClock.Utc));

    private static LedgerAccountService NewAccountService(AppDbContext db) => new(
        db,
        new LedgerAccountBalanceService(db, new CycleBalanceService(db), FinancialClock.Utc),
        new CycleBalanceService(db),
        FinancialClock.Utc);

    private static SavingsGoalService NewGoalService(AppDbContext db) => new(
        db,
        new CycleBalanceService(db),
        FinancialClock.Utc,
        new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance));
}
