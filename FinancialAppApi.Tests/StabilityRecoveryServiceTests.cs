using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Stability;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

/// <summary>
/// The EF half of emergency-fund recovery: carrying the explicit reload obligation through the
/// cycle-balance cache and keeping the income path's view of the balance honest when a salary is
/// being re-saved.
/// </summary>
public class StabilityRecoveryServiceTests
{
    private const int CycleDay = 1;

    [Fact]
    public async Task BuildAsync_AsksNothingOfAFundThatHasOnlyEverGoneUp()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 5, 4), "Stability", 800m);
        Add(context, "in-2", new DateTime(2026, 6, 4), "Stability", 700m);
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 1500m, current: 1500m);

        Assert.False(recovery.IsActive);
        Assert.Equal(0m, Money(recovery.OutstandingShortfall));
    }

    [Fact]
    public async Task BuildAsync_SpreadsWhatWasSpentAcrossTheRecoveryWindow()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 4, 4), "Stability", 3000m);
        Add(context, "out-1", new DateTime(2026, 6, 4), "Stability", -900m);
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 2100m, current: 2100m);

        Assert.True(recovery.IsActive);
        Assert.Equal(900m, Money(recovery.OutstandingShortfall));
        Assert.Equal("2026-06", recovery.LastDrawdownCycleKey);
        Assert.Equal(900m, Money(recovery.MarkedTotal));
        // One cycle of the three-cycle window has already elapsed.
        Assert.Equal(2, recovery.CyclesRemaining);
        Assert.Equal(450m, Money(recovery.RequiredThisCycle));
    }

    /// <summary>
    /// The window the client filters the ledger by starts at the exact oldest marked drawdown that
    /// is still outstanding, including any repayment made earlier in that same cycle.
    /// </summary>
    [Fact]
    public async Task BuildAsync_OpensTheWindowAfterTheFundWasLastFull()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 4, 4), "Stability", 3000m);
        Add(context, "out-1", new DateTime(2026, 6, 4), "Stability", -900m);
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 2100m, current: 2100m);

        Assert.Equal("2026-06-04", recovery.RecoveryFromDate);
    }

    /// <summary>
    /// A withdrawal in the cycle being viewed has no settled row of its own yet, so it has to be
    /// read straight off the ledger or the card would not appear until the cycle turned over.
    /// </summary>
    /// <summary>
    /// Marked and repaid must account for the same money the shortfall does. FIFO repayment retires
    /// the oldest drawdown first, so the 70 on 08-06 was cleared outright and left the queue; a
    /// window anchored on the outstanding head then started at 08-09 and reported 801.77 marked
    /// with 450 back, next to a reimbursement of 520 the user could see on the same card.
    /// </summary>
    [Fact]
    public async Task BuildAsync_CountsADrawdownThisCycleRepaidInFull()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        var opening = Add(context, "in-1", new DateTime(2026, 5, 4), "Stability", 4706.75m);
        var medical = Add(context, "out-1", new DateTime(2026, 8, 6), "Stability", -70m);
        var roadTax = Add(context, "out-2", new DateTime(2026, 8, 9), "Stability", -125m);
        var insurance = Add(context, "out-3", new DateTime(2026, 8, 9), "Stability", -676.77m);
        var reimburse = Add(context, "in-2", new DateTime(2026, 8, 9), "Stability", 520m, "Reimbursement");
        await context.SaveChangesAsync();

        var recovery = await Build(
            context, setting, 2026, 8,
            opening: 4706.75m, current: 4354.98m,
            medical, roadTax, insurance, reimburse);

        Assert.Equal(351.77m, Money(recovery.OutstandingShortfall));
        Assert.Equal(871.77m, Money(recovery.MarkedTotal));
        Assert.Equal(520m, Money(recovery.RepaidTotal));
        Assert.Equal(520m, Money(recovery.ToppedUpThisCycle));
        // The jump lands on the oldest row the figures were measured over, not on the 09th.
        Assert.Equal("2026-08-06", recovery.RecoveryFromDate);
        Assert.NotNull(opening);
    }

    [Fact]
    public async Task BuildAsync_SeesADrawdownMadeInTheCycleOnScreen()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 5, 4), "Stability", 2000m);
        var thisCycle = Add(context, "out-1", new DateTime(2026, 7, 4), "Stability", -500m);
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 2000m, current: 1500m, thisCycle);

        Assert.Equal("2026-07", recovery.LastDrawdownCycleKey);
        Assert.Equal(3, recovery.CyclesRemaining);
        Assert.Equal(500m, Money(recovery.OutstandingShortfall));
        // Asserting only the shortfall is what let the pace bug through: the card also gates on
        // this cycle's share, and anchoring that on the opening balance made it zero for a
        // drawdown made during the cycle -- so the card stayed hidden with a real shortfall on
        // screen behind it.
        Assert.True(Money(recovery.OutstandingThisCycle) > 0m);
        Assert.True(recovery.IsActive);
    }

    /// <summary>
    /// Cached rows only record where the fund stood at a cycle boundary. A bonus paid into the fund
    /// and then dipped into within the same cycle must still create a marked obligation.
    /// </summary>
    [Fact]
    public async Task BuildAsync_CountsAPeakReachedAndSpentInsideOneCycle()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 5, 4), "Stability", 1000m);
        var bonus = Add(context, "bonus", new DateTime(2026, 7, 2), "Stability", 3000m);
        var spend = Add(context, "spend", new DateTime(2026, 7, 20), "Stability", -1200m);
        await context.SaveChangesAsync();

        // Ends the cycle at 2,800 -- above the 1,000 it opened on, so cycle-ending rows alone see
        // no fall. The fund really did reach 4,000 and really did lose 1,200 of it.
        var recovery = await Build(context, setting, 2026, 7, opening: 1000m, current: 2800m, bonus, spend);

        Assert.Equal(1200m, Money(recovery.OutstandingShortfall));
        Assert.True(recovery.IsActive);
    }

    /// <summary>
    /// All three shapes a withdrawal takes go through one rule, because they all go through the
    /// same attribution function that produces the balance itself.
    /// </summary>
    [Theory]
    [InlineData("Stability", -400d, "Other")]
    [InlineData("Transfer:Stability->Rewards", 400d, "Transfer")]
    [InlineData("Stability", -400d, "Adjustment")]
    public async Task BuildAsync_CountsEveryShapeOfWithdrawal(string ledgerCategory, double amount, string category)
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 5, 4), "Stability", 2000m);
        var withdrawal = Add(context, "out-1", new DateTime(2026, 7, 4), ledgerCategory, (decimal)amount, category);
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 2000m, current: 1600m, withdrawal);

        Assert.True(recovery.IsActive);
        Assert.Equal(400m, Money(recovery.MarkedTotal));
    }

    [Fact]
    public async Task BuildAsync_DoesNotAskForADrawdownMarkedSpentForGood()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 5, 4), "Stability", 2000m);
        Add(
            context,
            "out-1",
            new DateTime(2026, 7, 4),
            "Stability",
            -400m,
            intent: StabilityReloadIntent.NotRequired);
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 2000m, current: 1600m);

        Assert.False(recovery.IsActive);
        Assert.Equal(0m, Money(recovery.OutstandingShortfall));
    }

    [Fact]
    public async Task BuildAsync_KeepsMarkedAndRepaidTotalsAcrossCycles()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 5, 4), "Stability", 1000m);
        Add(context, "out-1", new DateTime(2026, 6, 4), "Stability", -500m);
        var repayment = Add(
            context,
            "in-2",
            new DateTime(2026, 7, 4),
            "Transfer:Rewards->Stability",
            200m,
            "Transfer");
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 500m, current: 700m, repayment);

        Assert.Equal(500m, Money(recovery.MarkedTotal));
        Assert.Equal(200m, Money(recovery.RepaidTotal));
        Assert.Equal(300m, Money(recovery.OutstandingShortfall));
        Assert.Equal("2026-06-04", recovery.RecoveryFromDate);
    }

    [Fact]
    public async Task BuildAsync_IgnoresMoneyMovingIntoTheFund()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 5, 4), "Stability", 2000m);
        var topUp = Add(context, "in-2", new DateTime(2026, 7, 4), "Transfer:Rewards->Stability", 300m, "Transfer");
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 2000m, current: 2300m, topUp);

        Assert.False(recovery.IsActive);
        Assert.Null(recovery.LastDrawdownCycleKey);
    }

    /// <summary>
    /// The ordering trap. InvalidateFromAsync deletes cached rows on every transaction mutation, so
    /// reading reload state before rebuilding the cache under-reports the obligation.
    /// </summary>
    [Fact]
    public async Task BuildAsync_RebuildsTheCacheBeforeReadingReloadState()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 4, 4), "Stability", 3000m);
        Add(context, "out-1", new DateTime(2026, 6, 4), "Stability", -900m);
        await context.SaveChangesAsync();

        var cycleBalanceService = new CycleBalanceService(context);
        await cycleBalanceService.EnsureComputedThroughAsync(2026, 7, CycleDay);
        // Simulates the state right after any transaction edit: the whole cache is gone.
        await cycleBalanceService.InvalidateFromAsync(2026, 1);
        Assert.Empty(await context.CycleBalances.ToListAsync());

        var recovery = await Build(context, setting, 2026, 7, opening: 2100m, current: 2100m);

        Assert.Equal(900m, Money(recovery.OutstandingShortfall));
    }

    [Fact]
    public async Task BuildAsync_ReportsWhatSavingsGoalsStillNeedThisCycle()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        context.SavingsGoals.Add(new SavingsGoal
        {
            Name = "Car service",
            TargetAmount = 600m,
            EarmarkedAmount = 0m,
            TargetDate = new DateTime(2026, 7, 20),
            Status = SavingsGoalStatus.Active,
            Priority = "Medium"
        });
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 0m, current: 0m);

        // Due inside the current cycle, so the whole 600 is owed now and the draw must stay above it.
        Assert.Equal(600m, Money(recovery.RewardsCommitted));
    }

    [Fact]
    public async Task BuildAsync_PutsGoalPaceIntoItsFundingBucket()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        context.SavingsGoals.AddRange(
            new SavingsGoal
            {
                Name = "Essentials reserve",
                TargetAmount = 600m,
                EarmarkedAmount = 0m,
                TargetDate = new DateTime(2026, 7, 20),
                Status = SavingsGoalStatus.Active,
                Priority = "Medium",
                FundingBucket = SavingsGoalFundingBucket.Essentials
            },
            new SavingsGoal
            {
                Name = "Rewards reserve",
                TargetAmount = 400m,
                EarmarkedAmount = 0m,
                TargetDate = new DateTime(2026, 7, 20),
                Status = SavingsGoalStatus.Active,
                Priority = "Medium",
                FundingBucket = SavingsGoalFundingBucket.Rewards
            });
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 0m, current: 0m);

        Assert.Equal(600m, Money(recovery.EssentialsCommitted));
        Assert.Equal(400m, Money(recovery.RewardsCommitted));
    }

    /// <summary>
    /// A goal's pace is measured from today's deadline distance and its tally is keyed to the live
    /// cycle, so asking it about a closed cycle reports a commitment that cannot still be owed.
    /// Only the current cycle can carry a top-up offer, so the figure has no reader there anyway.
    /// </summary>
    [Fact]
    public async Task BuildAsync_LeavesGoalCommitmentsOutOfACycleThatHasAlreadyClosed()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        context.SavingsGoals.Add(new SavingsGoal
        {
            Name = "Car service",
            TargetAmount = 600m,
            EarmarkedAmount = 0m,
            TargetDate = new DateTime(2026, 7, 20),
            Status = SavingsGoalStatus.Active,
            Priority = "Medium"
        });
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 5, opening: 0m, current: 0m);

        Assert.Equal(0m, Money(recovery.RewardsCommitted));
    }

    [Fact]
    public async Task BuildAsync_IncludesPendingRewardsBillsInTheRecoveryFloor()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        await context.SaveChangesAsync();

        var recovery = await BuildWithRewards(
            context,
            setting,
            2026,
            7,
            opening: 0m,
            current: 0m,
            rewardsRecurringCommitted: 150m);

        Assert.Equal(150m, Money(recovery.RewardsCommitted));
    }

    [Fact]
    public async Task BuildAsync_SplitsAProposedTopUpAcrossTheThreeContributingBuckets()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 0m, current: 0m);

        Assert.Equal(new[] { "Essentials", "Growth", "Rewards" }, recovery.SuggestedDraws.Select(draw => draw.Bucket));
        Assert.Equal(1m, recovery.SuggestedDraws.Sum(draw => draw.Share));
    }

    [Fact]
    public async Task GetStabilityStateAsync_ReadsTheLiveBalance()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 7, 4), "Stability", 250m);
        await context.SaveChangesAsync();

        var state = await NewService(context).GetStabilityStateAsync(setting);

        Assert.Equal(250m, state.CurrentBalance);
        Assert.Equal(10000m, state.Target);
    }

    /// <summary>
    /// Re-saving a salary must be measured against the fund WITHOUT its own earlier contribution.
    /// Counting it would make an edit look like it had already filled the headroom it is asking for.
    /// </summary>
    [Fact]
    public async Task GetStabilityStateAsync_ExcludesTheTransactionBeingSavedAndItsSplitChildren()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "salary", new DateTime(2026, 7, 4), "IncomeSplit:50,25,15,10", 1000m);
        Add(context, "salary-split-Stability", new DateTime(2026, 7, 4), "Transfer:Income->Stability", 150m, "Transfer");
        Add(context, "other", new DateTime(2026, 7, 5), "Stability", 40m);
        await context.SaveChangesAsync();

        var state = await NewService(context).GetStabilityStateAsync(setting, "salary");

        // Only the unrelated 40 survives: both the parent's own 15% share and the child row it
        // generated are excluded, and neither is double counted.
        Assert.Equal(40m, state.CurrentBalance);
    }

    private static async Task<StabilityRecoveryDto> Build(
        AppDbContext context,
        FinancialSetting setting,
        int year,
        int monthIndex,
        decimal opening,
        decimal current,
        params Transaction[] activeCycleTxs)
    {
        return await BuildWithRewards(context, setting, year, monthIndex, opening, current, 0m, activeCycleTxs);
    }

    private static async Task<StabilityRecoveryDto> BuildWithRewards(
        AppDbContext context,
        FinancialSetting setting,
        int year,
        int monthIndex,
        decimal opening,
        decimal current,
        decimal rewardsRecurringCommitted,
        params Transaction[] activeCycleTxs)
    {
        return await NewService(context).BuildAsync(
            setting,
            year,
            monthIndex,
            activeCycleTxs,
            opening,
            current,
            essentialsCommitted: 0m,
            rewardsRecurringCommitted: rewardsRecurringCommitted);
    }

    private static StabilityRecoveryService NewService(AppDbContext context)
    {
        return new StabilityRecoveryService(
            context,
            new CycleBalanceService(context),
            new FinancialClock(
                TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
                new FixedTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero))));
    }

    private static AppDbContext NewContext() => TestHelpers.NewInMemoryContext();

    private static FinancialSetting SeedSetting(AppDbContext context, decimal target)
    {
        var setting = new FinancialSetting
        {
            EssentialsAlloc = 0.50m,
            GrowthAlloc = 0.25m,
            StabilityAlloc = 0.15m,
            RewardsAlloc = 0.10m,
            TargetStabilityFund = target,
            StabilityOverflowRedirect = StabilityOverflowRedirectOptions.GrowthRewards,
            CycleDay = CycleDay
        };
        context.FinancialSettings.Add(setting);
        return setting;
    }

    private static Transaction Add(
        AppDbContext context,
        string id,
        DateTime date,
        string ledgerCategory,
        decimal amount,
        string category = "Other",
        string intent = StabilityReloadIntent.Unanswered)
    {
        var transaction = new Transaction
        {
            Id = id,
            Date = DateTime.SpecifyKind(date, DateTimeKind.Utc),
            Description = id,
            Category = category,
            LedgerCategory = ledgerCategory,
            Amount = amount,
            StabilityReloadIntent = intent
        };
        context.Transactions.Add(transaction);
        return transaction;
    }

    private static decimal Money(string obfuscated) => ObfuscationHelper.Deobfuscate(obfuscated);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
