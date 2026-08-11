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
        string ledgerCategory = "Stability") =>
        context.Transactions.Add(new Transaction
        {
            Id = id,
            Date = DateTime.SpecifyKind(date, DateTimeKind.Utc),
            PostedAt = DateTime.SpecifyKind(date, DateTimeKind.Utc),
            Description = id,
            Category = "Other",
            LedgerCategory = ledgerCategory,
            Amount = amount,
            StabilityReloadIntent = intent,
        });
}
