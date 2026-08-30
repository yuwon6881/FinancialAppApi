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
    public async Task BuildAsync_GivesASecondCycleDrawdownItsOwnRecoveryCohort()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "opening", new DateTime(2026, 4, 4), "Stability", 3000m);
        Add(context, "old-drawdown", new DateTime(2026, 6, 4), "Stability", -900m);
        Add(context, "old-repayment", new DateTime(2026, 6, 20), "Stability", 300m, "Reimbursement");
        var newDrawdown = Add(context, "new-drawdown", new DateTime(2026, 7, 4), "Stability", -300m);
        var currentRepayment = Add(
            context, "current-repayment", new DateTime(2026, 7, 20), "Stability", 100m, "Reimbursement");
        await context.SaveChangesAsync();

        var recovery = await Build(
            context,
            setting,
            2026,
            7,
            opening: 2400m,
            current: 2200m,
            newDrawdown,
            currentRepayment);

        Assert.Equal(800m, Money(recovery.OutstandingShortfall));
        Assert.Equal(400m, Money(recovery.RequiredThisCycle));
        Assert.Equal(100m, Money(recovery.ToppedUpThisCycle));
        Assert.Equal(300m, Money(recovery.OutstandingThisCycle));
        Assert.Equal(["2026-06", "2026-07"], recovery.RecoveryCohorts.Select(cohort => cohort.OriginCycleKey));
        Assert.Equal([2, 3], recovery.RecoveryCohorts.Select(cohort => cohort.CyclesRemaining));
        Assert.Equal(
            [300m, 100m],
            recovery.RecoveryCohorts.Select(cohort => Money(cohort.RequiredThisCycle)));
    }

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
    /// Marked and repaid account for exactly the money the shortfall does. FIFO repayment retires
    /// the oldest drawdown first, so the 70 on 08-06 was put back outright and leaves both figures:
    /// what is still asked back is the 676.77 less the 325 that went against it. The whole-cycle
    /// activity -- 871.77 marked, 520 back -- is a different question, and reporting it here is what
    /// let a settled drawdown keep inflating the ask.
    /// </summary>
    [Fact]
    public async Task BuildAsync_DropsADrawdownAlreadyPutBackInFull()
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
        Assert.Equal(676.77m, Money(recovery.MarkedTotal));
        Assert.Equal(325m, Money(recovery.RepaidTotal));
        Assert.Equal(
            Money(recovery.OutstandingShortfall),
            Money(recovery.MarkedTotal) - Money(recovery.RepaidTotal));
        // What went back during the cycle is a separate figure and still counts the whole 520.
        Assert.Equal(520m, Money(recovery.ToppedUpThisCycle));
        // The jump lands on the oldest row that still owes, which is the only row the figures cover.
        Assert.Equal("2026-08-09", recovery.RecoveryFromDate);
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

    /// <summary>
    /// A carried drawdown that this cycle's repayment cleared outright leaves the reported figures
    /// entirely, taking the window with it. Counting it kept the December 500 in the ask forever:
    /// the total only ever grew, one drawdown at a time, and never came back down.
    /// </summary>
    [Fact]
    public async Task BuildAsync_DropsACarriedDrawdownPutBackInFull()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 5, 4), "Stability", 1000m);
        Add(context, "old-drawdown", new DateTime(2026, 6, 4), "Stability", -500m);
        var currentDrawdown = Add(context, "current-drawdown", new DateTime(2027, 1, 4), "Stability", -300m);
        var repayment = Add(
            context,
            "current-repayment",
            new DateTime(2027, 1, 4),
            "Stability",
            600m,
            "Reimbursement");
        // Build starts from the persisted previous-cycle queue. Seed the carried 500 explicitly so
        // this test isolates the cross-cycle reporting window from the cache-building path.
        context.CycleBalances.Add(new CycleBalance
        {
            Year = 2026,
            MonthIndex = 12,
            StabilityBalance = 500m,
            StabilityReloadOutstanding = 500m,
            StabilityReloadOldestDate = new DateOnly(2026, 6, 4)
        });
        await context.SaveChangesAsync();

        var recovery = await Build(
            context,
            setting,
            2027,
            1,
            opening: 500m,
            current: 800m,
            currentDrawdown,
            repayment);

        // The carried 500 is fully back, so only the current 300 is still asked about -- 100 of
        // which the same repayment already covered.
        Assert.Equal(300m, Money(recovery.MarkedTotal));
        Assert.Equal(100m, Money(recovery.RepaidTotal));
        Assert.Equal(200m, Money(recovery.OutstandingShortfall));
        Assert.Equal("2027-01-04", recovery.RecoveryFromDate);
        // The pace anchors on what is still owed, so a settled June drawdown cannot report the
        // recovery as overdue seven cycles later.
        Assert.Equal("2027-01", recovery.LastDrawdownCycleKey);
        Assert.False(recovery.IsOverdue);
    }

    /// <summary>
    /// The client replays the current cycle itself and needs the same opening queue the server used.
    /// Sending only the aggregate leaves it unable to tell a partly-repaid carried drawdown from one
    /// already put back in full, which is the same defect one layer out.
    /// </summary>
    [Fact]
    public async Task BuildAsync_SendsTheCarriedObligationsTheClientHasToReplayFrom()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 5, 4), "Stability", 1000m);
        Add(context, "old-drawdown", new DateTime(2026, 6, 4), "Stability", -500m);
        context.CycleBalances.Add(new CycleBalance
        {
            Year = 2026,
            MonthIndex = 12,
            StabilityBalance = 500m,
            StabilityReloadOutstanding = 300m,
            StabilityReloadOldestDate = new DateOnly(2026, 6, 4),
            StabilityReloadObligations = StabilityReloadObligationCache.Serialize(
            [
                new ReloadObligation("settled", 200m, 0m, new DateOnly(2026, 6, 1)),
                new ReloadObligation("old-drawdown", 500m, 300m, new DateOnly(2026, 6, 4))
            ])
        });
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2027, 1, opening: 500m, current: 500m);

        // Only the still-owing entry travels; the settled one has nothing to carry.
        var carried = Assert.Single(recovery.OpeningObligations);
        Assert.Equal("old-drawdown", carried.TransactionId);
        Assert.Equal(500m, Money(carried.OriginalAmount));
        Assert.Equal(300m, Money(carried.RemainingAmount));
        Assert.Equal("2026-06-04", carried.Date);
        // And the reported figures agree with it: 500 asked back, 200 of it already returned.
        Assert.Equal(500m, Money(recovery.MarkedTotal));
        Assert.Equal(200m, Money(recovery.RepaidTotal));
        Assert.Equal(300m, Money(recovery.OutstandingShortfall));
    }

    /// <summary>
    /// The other half of the same rule: a carried drawdown only partly put back stays in the figures
    /// at its full original amount, with what has gone back reported against it.
    /// </summary>
    [Fact]
    public async Task BuildAsync_KeepsACarriedDrawdownOnlyPartlyPutBack()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 5, 4), "Stability", 1000m);
        Add(context, "old-drawdown", new DateTime(2026, 6, 4), "Stability", -500m);
        var repayment = Add(
            context,
            "current-repayment",
            new DateTime(2027, 1, 4),
            "Stability",
            200m,
            "Reimbursement");
        // Carried with its identity, which is what lets the 500 be reported as a 500 partly back
        // rather than as an anonymous 300 still owed.
        context.CycleBalances.Add(new CycleBalance
        {
            Year = 2026,
            MonthIndex = 12,
            StabilityBalance = 500m,
            StabilityReloadOutstanding = 500m,
            StabilityReloadOldestDate = new DateOnly(2026, 6, 4),
            StabilityReloadObligations = StabilityReloadObligationCache.Serialize(
                [new ReloadObligation("old-drawdown", 500m, 500m, new DateOnly(2026, 6, 4))])
        });
        await context.SaveChangesAsync();

        var recovery = await Build(
            context, setting, 2027, 1, opening: 500m, current: 700m, repayment);

        Assert.Equal(500m, Money(recovery.MarkedTotal));
        Assert.Equal(200m, Money(recovery.RepaidTotal));
        Assert.Equal(300m, Money(recovery.OutstandingShortfall));
        Assert.Equal("2026-06-04", recovery.RecoveryFromDate);
        Assert.Equal("2026-06", recovery.LastDrawdownCycleKey);
    }

    /// <summary>
    /// The reported defect, end to end: an unfinished put-back followed by a fresh drawdown must not
    /// make the total climb. Each new drawdown used to be added to a running sum that never dropped
    /// the ones already settled.
    /// </summary>
    [Fact]
    public async Task BuildAsync_DoesNotAccumulateSettledDrawdownsAcrossCycles()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 5, 4), "Stability", 2000m);
        // Cycle one: 500 out, 400 back. 100 of the 500 is still owed.
        Add(context, "out-1", new DateTime(2026, 6, 4), "Stability", -500m);
        Add(context, "back-1", new DateTime(2026, 6, 20), "Stability", 400m, "Reimbursement");
        // Cycle two: 300 more out, then 100 back -- which settles the first drawdown outright.
        var secondDrawdown = Add(context, "out-2", new DateTime(2026, 7, 4), "Stability", -300m);
        var secondRepayment = Add(
            context, "back-2", new DateTime(2026, 7, 20), "Stability", 100m, "Reimbursement");
        await context.SaveChangesAsync();

        var recovery = await Build(
            context, setting, 2026, 7,
            opening: 1900m, current: 1700m,
            secondDrawdown, secondRepayment);

        // Only the second drawdown still owes anything, and nothing has gone against it.
        Assert.Equal(300m, Money(recovery.MarkedTotal));
        Assert.Equal(0m, Money(recovery.RepaidTotal));
        Assert.Equal(300m, Money(recovery.OutstandingShortfall));
        Assert.Equal("2026-07-04", recovery.RecoveryFromDate);
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
    public async Task BuildAsync_RebuildsMalformedPositiveOpeningObligationsFromHistory()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 4, 4), "Stability", 1000m);
        Add(context, "old-drawdown", new DateTime(2026, 6, 4), "Stability", -300m);
        context.CycleBalances.Add(new CycleBalance
        {
            Year = 2026,
            MonthIndex = 6,
            StabilityBalance = 700m,
            StabilityReloadOutstanding = 300m,
            StabilityReloadOldestDate = new DateOnly(2026, 6, 4),
            StabilityReloadObligations = null
        });
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 700m, current: 700m);

        var cohort = Assert.Single(recovery.RecoveryCohorts);
        Assert.Equal("2026-06", cohort.OriginCycleKey);
        Assert.Equal(2, cohort.CyclesRemaining);
        Assert.Equal(150m, Money(cohort.RequiredThisCycle));
        var rebuiltJune = await context.CycleBalances.SingleAsync(
            balance => balance.Year == 2026 && balance.MonthIndex == 6);
        Assert.False(string.IsNullOrWhiteSpace(rebuiltJune.StabilityReloadObligations));
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

    [Fact]
    public async Task BuildAsync_CarriedDrawdownAcrossMultipleCycles_RetainsRecoveryFromDateAndMarkedTotalThroughCache()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 4, 4), "Stability", 5000m);
        Add(context, "drawdown-1", new DateTime(2026, 5, 10), "Stability", -500m);
        await context.SaveChangesAsync();

        // Advance 3 cycles without repaying. Cache rebuilds across multiple cycles.
        var recovery = await Build(context, setting, 2026, 8, opening: 4500m, current: 4500m);

        Assert.True(recovery.IsActive);
        Assert.Equal(500m, Money(recovery.OutstandingShortfall));
        Assert.Equal(500m, Money(recovery.MarkedTotal));
        Assert.Equal(0m, Money(recovery.RepaidTotal));
        Assert.Equal("2026-05-10", recovery.RecoveryFromDate);
    }

    [Fact]
    public async Task BuildAsync_NegativeBalanceCorrectionContributesNothingToMarkedOrRepaidTotals()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 10000m);
        Add(context, "in-1", new DateTime(2026, 6, 4), "Stability", 5000m);
        var correction = Add(
            context,
            "corr-1",
            new DateTime(2026, 7, 4),
            "Stability",
            -200m,
            category: "Adjustment",
            isAccountBalanceAdjustment: true);
        await context.SaveChangesAsync();

        var recovery = await Build(context, setting, 2026, 7, opening: 5000m, current: 4800m, correction);

        Assert.False(recovery.IsActive);
        Assert.Equal(0m, Money(recovery.MarkedTotal));
        Assert.Equal(0m, Money(recovery.RepaidTotal));
        Assert.Equal(0m, Money(recovery.OutstandingShortfall));
    }

    [Fact]
    public async Task BuildAsync_CycleClearedByAttainmentDoesNotRestartRecoveryWindow()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 5000m);
        Add(context, "in-1", new DateTime(2026, 4, 4), "Stability", 5000m);
        // Cycle 2026-05: drawdown -500, then +500 reaches 5000 target and clears queue
        Add(context, "drawdown-1", new DateTime(2026, 5, 2), "Stability", -500m);
        Add(context, "deposit-1", new DateTime(2026, 5, 10), "Stability", 500m);
        // Cycle 2026-06: no drawdowns
        await context.SaveChangesAsync();

        var cycleBalanceService = new CycleBalanceService(context);
        await cycleBalanceService.EnsureComputedThroughAsync(2026, 7, CycleDay);

        var recovery = await Build(context, setting, 2026, 7, opening: 5000m, current: 5000m);

        Assert.False(recovery.IsActive);
        Assert.Null(recovery.LastDrawdownCycleKey);
    }

    [Fact]
    public async Task BuildAsync_OrdinarySalaryThatRestoresTheTargetCompletesACarriedObligation()
    {
        await using var context = NewContext();
        var setting = SeedSetting(context, target: 1000m);
        Add(context, "opening", new DateTime(2026, 5, 4), "Stability", 1000m);
        Add(context, "prior-drawdown", new DateTime(2026, 6, 4), "Stability", -300m);

        var salary = Add(context, "salary", new DateTime(2026, 7, 4), "Income", 2000m);
        salary.StabilityRecoveryTopUpAmount = 0m;
        var stabilityShare = Add(
            context,
            "salary-split-Stability",
            new DateTime(2026, 7, 4),
            "Transfer:Income->Stability",
            300m,
            category: "Transfer");
        await context.SaveChangesAsync();

        var recovery = await Build(
            context,
            setting,
            2026,
            7,
            opening: 700m,
            current: 1000m,
            salary,
            stabilityShare);

        Assert.False(recovery.IsActive);
        Assert.Equal(0m, Money(recovery.OutstandingShortfall));
        Assert.Equal(0m, Money(recovery.MarkedTotal));
        Assert.Equal(0m, Money(recovery.RepaidTotal));
        Assert.Null(recovery.RecoveryFromDate);
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
        string intent = StabilityReloadIntent.Unanswered,
        bool isAccountBalanceAdjustment = false)
    {
        var transaction = new Transaction
        {
            Id = id,
            Date = DateTime.SpecifyKind(date, DateTimeKind.Utc),
            Description = id,
            Category = category,
            LedgerCategory = ledgerCategory,
            Amount = amount,
            StabilityReloadIntent = intent,
            IsAccountBalanceAdjustment = isAccountBalanceAdjustment
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
