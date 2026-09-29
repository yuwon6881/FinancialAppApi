using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;
using FinancialAppApi.Services.AI;
using FinancialAppApi.Services.AI.Agent;
using FinancialAppApi.Services.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using static FinancialAppApi.Tests.AiTools.AiToolTestSupport;

namespace FinancialAppApi.Tests.AiTools;

public class AiBaselineSnapshotTests
{
    [Fact]
    public async Task Snapshot_GivesEveryTurnTheCycleCalendarAccountsCurrentCycleAndUpcomingBills()
    {
        await using var db = await SeedAsync();

        var snapshot = await NewBuilder(db).BuildAsync(NewContext(), CancellationToken.None);

        Assert.Equal("2026-09-29", snapshot["today"]!.GetValue<string>());
        var cycles = snapshot["cycles"]!.AsArray();
        Assert.Equal(12, cycles.Count);
        Assert.Equal("2026-09", cycles[0]!["key"]!.GetValue<string>());
        Assert.Equal("current", cycles[0]!["label"]!.GetValue<string>());
        Assert.Equal("2026-08-25", cycles[1]!["from"]!.GetValue<string>());
        Assert.Equal("2025-11-03", snapshot["firstTransactionDate"]!.GetValue<string>());
        Assert.Equal(137.29m, snapshot["currentCycle"]!["outflow"]!.GetValue<decimal>());
        Assert.Equal("Food", snapshot["currentCycle"]!["topSpending"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Spotify", snapshot["upcomingBills"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(4, snapshot["accounts"]!["accounts"]!.AsArray().Count);
    }

    [Fact]
    public async Task Snapshot_InSensitiveModeKeepsStructureButNoAmounts()
    {
        await using var db = await SeedAsync();

        var snapshot = (await NewBuilder(db).BuildAsync(NewContext(sensitive: true), CancellationToken.None)).ToJsonString();

        Assert.Contains("Spotify", snapshot);
        Assert.Contains("\"transactionCount\"", snapshot);
        foreach (var amount in new[] { "137.29", "5123.45", "15.97" })
            Assert.DoesNotContain(amount, snapshot);
    }

    [Fact]
    public async Task Snapshot_LeavesOutAPartThatFailsInsteadOfFailingTheTurn()
    {
        await using var db = await SeedAsync();
        var builder = new AiBaselineSnapshotBuilder(
            new AiToolRegistry([new ThrowingTool("get_accounts")]),
            new AiTransactionQueryService(db),
            NullLogger<AiBaselineSnapshotBuilder>.Instance);

        var snapshot = await builder.BuildAsync(NewContext(), CancellationToken.None);

        Assert.Null(snapshot["accounts"]);
        Assert.NotNull(snapshot["cycles"]);
    }

    private static async Task<AppDbContext> SeedAsync()
    {
        var db = TestHelpers.NewInMemoryContext();
        TestHelpers.SeedLedgerAccounts(db);
        db.Transactions.AddRange(
            Txn("old", new DateOnly(2025, 11, 3), "Haircut", -35m),
            Txn("salary", new DateOnly(2026, 8, 25), "Salary", 5123.45m, "Salary", "Income"),
            Txn("groceries", new DateOnly(2026, 9, 26), "Groceries", -137.29m, "Food"));
        db.RecurringPayments.Add(new RecurringPayment
        {
            Id = "spotify", Name = "Spotify", Amount = 15.97m, Frequency = "Monthly", Category = "Entertainment",
            LedgerCategory = "Rewards", DueDate = 20, StartDate = "2026-01-01", Active = true
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static AiBaselineSnapshotBuilder NewBuilder(AppDbContext db)
    {
        var transactions = new AiTransactionQueryService(db);
        var accounts = new LedgerAccountService(
            db, new LedgerAccountBalanceService(db, new CycleBalanceService(db), FinancialClock.Utc), new CycleBalanceService(db), FinancialClock.Utc);
        var occurrences = new RecurringOccurrenceLedgerService(
            db, new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance), FinancialClock.Utc);
        return new AiBaselineSnapshotBuilder(
            new AiToolRegistry(
            [
                new GetAccountsTool(accounts),
                new GetCycleSummaryTool(transactions),
                new GetRecurringTool(db, occurrences),
                new GetCategoryLimitsTool(db, transactions, new AiRecurringBillStatusService(db, occurrences))
            ]),
            transactions,
            NullLogger<AiBaselineSnapshotBuilder>.Instance);
    }

    private sealed class ThrowingTool(string name) : IAiTool
    {
        public string Name => name;
        public string Description => "throws";
        public System.Text.Json.Nodes.JsonObject ParametersSchema { get; } = AiToolSchema.Object([]);
        public AiToolSensitivity Sensitivity => AiToolSensitivity.None;
        public string ProgressLabel(AiToolArgs args) => "Throwing";
        public Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
