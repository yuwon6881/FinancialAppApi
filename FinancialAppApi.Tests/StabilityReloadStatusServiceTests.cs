using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Tests;

public sealed class StabilityReloadStatusServiceTests
{
    [Fact]
    public async Task GetStatusMapAsync_ReturnsTheFourPerWithdrawalStates()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedSetting(context, target: 0m);
        Add(context, "first", new DateTime(2026, 7, 1), -100m, StabilityReloadIntent.Required);
        Add(context, "second", new DateTime(2026, 7, 2), -80m, StabilityReloadIntent.Required);
        Add(context, "spent", new DateTime(2026, 7, 3), -20m, StabilityReloadIntent.NotRequired);
        Add(context, "repayment", new DateTime(2026, 7, 4), 50m, StabilityReloadIntent.Unanswered,
            ledgerCategory: "Transfer:Growth->Stability");
        await context.SaveChangesAsync();

        var statuses = await new StabilityReloadStatusService(context).GetStatusMapAsync();

        Assert.Equal(StabilityReloadStatus.PartlyRepaid, statuses["first"]);
        Assert.Equal(StabilityReloadStatus.Outstanding, statuses["second"]);
        Assert.Equal(StabilityReloadStatus.NotRequired, statuses["spent"]);
    }

    [Fact]
    public async Task GetStatusMapAsync_DoesNotReopenAWithdrawalAfterATargetIncrease()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedSetting(context, target: 1000m);
        Add(context, "opening", new DateTime(2026, 7, 1), 900m, StabilityReloadIntent.Unanswered);
        Add(context, "withdrawal", new DateTime(2026, 7, 1, 1, 0, 0), -100m, StabilityReloadIntent.Required);
        Add(context, "refill", new DateTime(2026, 7, 2), 200m, StabilityReloadIntent.Unanswered,
            ledgerCategory: "Transfer:Growth->Stability");
        context.StabilityPlanRevisions.Add(new StabilityPlanRevision
        {
            EffectiveAt = new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc),
            TargetStabilityFund = 2000m,
            StabilityAlloc = 0.15m,
        });
        await context.SaveChangesAsync();

        var statuses = await new StabilityReloadStatusService(context).GetStatusMapAsync();

        Assert.Equal(StabilityReloadStatus.Complete, statuses["withdrawal"]);
    }

    [Fact]
    public async Task GetStatusMapAsync_BalanceCorrectionGetsNoStatusEntry()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedSetting(context, target: 1000m);
        Add(context, "correction", new DateTime(2026, 7, 1), -100m, StabilityReloadIntent.Required, isAccountBalanceAdjustment: true);
        await context.SaveChangesAsync();

        var statuses = await new StabilityReloadStatusService(context).GetStatusMapAsync();

        Assert.False(statuses.ContainsKey("correction"));
    }

    /// <summary>
    /// The per-row status map and the dashboard's reported totals are produced by two different
    /// replays -- this one from the whole history, the dashboard's from the cycle cache -- and they
    /// have to agree about which drawdowns are settled. A divergence between two replays of this
    /// ledger is the worst money bug the app has had, and RebuildStabilityReloadCache exists
    /// because it was persisted before anyone noticed.
    /// </summary>
    [Fact]
    public async Task GetStatusMapAsync_AgreesWithTheReportedTotalsAboutWhatIsSettled()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedSetting(context, target: 0m);
        Add(context, "settled", new DateTime(2026, 7, 1), -100m, StabilityReloadIntent.Required);
        Add(context, "partly", new DateTime(2026, 7, 2), -80m, StabilityReloadIntent.Required);
        Add(context, "untouched", new DateTime(2026, 7, 3), -60m, StabilityReloadIntent.Required);
        Add(context, "spent", new DateTime(2026, 7, 3, 1, 0, 0), -20m, StabilityReloadIntent.NotRequired);
        Add(context, "repayment", new DateTime(2026, 7, 4), 130m, StabilityReloadIntent.Unanswered,
            ledgerCategory: "Transfer:Growth->Stability");
        await context.SaveChangesAsync();

        var statuses = await new StabilityReloadStatusService(context).GetStatusMapAsync();
        var transactions = context.Transactions.ToList();
        var replay = StabilityReloadLedger.Replay(
            new ReloadState(0m, null, 0m, 0m),
            openingBalance: 0m,
            target: 0m,
            StabilityReloadLedger.DescribeAll(transactions, 0.15m));
        var obligations = replay.Obligations!.ToDictionary(item => item.TransactionId);

        Assert.Equal(StabilityReloadStatus.Complete, statuses["settled"]);
        Assert.Equal(StabilityReloadStatus.PartlyRepaid, statuses["partly"]);
        Assert.Equal(StabilityReloadStatus.Outstanding, statuses["untouched"]);

        // Nothing the map calls complete may contribute to the reported ask.
        foreach (var (id, status) in statuses.Where(entry => entry.Value == StabilityReloadStatus.Complete))
        {
            Assert.Equal(0m, obligations[id].RemainingAmount);
        }

        // And what the map still shows as owing is exactly what the totals report.
        var owedByStatus = statuses
            .Where(entry => entry.Value is StabilityReloadStatus.Outstanding or StabilityReloadStatus.PartlyRepaid)
            .Sum(entry => obligations[entry.Key].RemainingAmount);
        Assert.Equal(replay.Outstanding, owedByStatus);
        Assert.Equal(replay.Outstanding, replay.OpenMarkedTotal - replay.OpenRepaidTotal);
        Assert.Equal(140m, replay.OpenMarkedTotal);
        Assert.Equal(30m, replay.OpenRepaidTotal);
    }

    private static FinancialSetting SeedSetting(AppDbContext context, decimal target)
    {
        var setting = new FinancialSetting
        {
            TargetStabilityFund = target,
            StabilityAlloc = 0.15m,
            EssentialsAlloc = 0.50m,
            GrowthAlloc = 0.25m,
            RewardsAlloc = 0.10m,
            CycleDay = 1,
        };
        context.FinancialSettings.Add(setting);
        return setting;
    }

    private static void Add(
        AppDbContext context,
        string id,
        DateTime date,
        decimal amount,
        string intent,
        string ledgerCategory = "Stability",
        bool isAccountBalanceAdjustment = false) =>
        context.Transactions.Add(new Transaction
        {
            Id = id,
            Date = DateTime.SpecifyKind(date, DateTimeKind.Utc),
            PostedAt = DateTime.SpecifyKind(date, DateTimeKind.Utc),
            Description = id,
            Category = isAccountBalanceAdjustment ? "Adjustment" : "Other",
            LedgerCategory = ledgerCategory,
            Amount = amount,
            StabilityReloadIntent = intent,
            IsAccountBalanceAdjustment = isAccountBalanceAdjustment,
        });
}
