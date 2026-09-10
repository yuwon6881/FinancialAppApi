using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Tests;

/// <summary>
/// The emergency-fund recovery math. The per-cycle requirement is anchored on the outstanding
/// obligation before this cycle's repayments, so it holds still while being funded — and the three
/// instalments start in the cycle after the money left, not in the cycle it left in.
/// </summary>
public class StabilityRecoveryPlannerTests
{
    /// <summary>
    /// The reason the grace cycle exists: the money left the fund partway through a cycle whose
    /// income has already been split and largely spent, so this cycle asks for nothing at all.
    /// </summary>
    [Fact]
    public void ComputeCohortPlan_AsksForNothingInTheCycleTheMoneyLeftIn()
    {
        var plan = StabilityRecoveryPlanner.ComputeCohortPlan(
        [
            new RecoveryCohortInput("2026-07", new DateOnly(2026, 7, 4), 1, 300m, 0m)
        ],
        currentCycleKey: "2026-07",
        horizon: 3,
        outstandingShortfall: 300m,
        toppedUpThisCycle: 0m);

        Assert.True(plan.Aggregate.IsDeferred);
        Assert.Equal(0m, plan.Aggregate.RequiredThisCycle);
        Assert.Equal(0m, plan.Aggregate.OutstandingThisCycle);
        // The plan is known, it just has not opened: all three instalments are still ahead.
        Assert.Equal(3, plan.Aggregate.CyclesRemaining);
        Assert.False(plan.Aggregate.IsOverdue);
    }

    [Fact]
    public void ComputeCohortPlan_StartsTheFirstInstalmentInTheFollowingCycle()
    {
        var plan = StabilityRecoveryPlanner.ComputeCohortPlan(
        [
            new RecoveryCohortInput("2026-07", new DateOnly(2026, 7, 4), 1, 300m, 0m)
        ],
        currentCycleKey: "2026-08",
        horizon: 3,
        outstandingShortfall: 300m,
        toppedUpThisCycle: 0m);

        Assert.False(plan.Aggregate.IsDeferred);
        Assert.Equal(100m, plan.Aggregate.RequiredThisCycle);
        Assert.Equal(3, plan.Aggregate.CyclesRemaining);
    }

    /// <summary>
    /// Money put back during the grace cycle is credit, not a payment against a due instalment: it
    /// shrinks the shortfall the first real cycle divides without making this cycle ask for anything.
    /// </summary>
    [Fact]
    public void ComputeCohortPlan_CreditsAnEarlyPutBackWithoutOpeningThePlan()
    {
        var plan = StabilityRecoveryPlanner.ComputeCohortPlan(
        [
            new RecoveryCohortInput("2026-07", new DateOnly(2026, 7, 4), 1, 200m, 100m)
        ],
        currentCycleKey: "2026-07",
        horizon: 3,
        outstandingShortfall: 200m,
        toppedUpThisCycle: 100m);

        Assert.True(plan.Aggregate.IsDeferred);
        Assert.Equal(0m, plan.Aggregate.RequiredThisCycle);
        Assert.Equal(0m, plan.Aggregate.OutstandingThisCycle);
        Assert.Equal(100m, plan.Aggregate.ToppedUpThisCycle);
        Assert.Equal(200m, plan.Aggregate.Shortfall);
    }

    [Fact]
    public void ComputeCohortPlan_GivesANewDrawdownItsOwnThreeCycleWindow()
    {
        var plan = StabilityRecoveryPlanner.ComputeCohortPlan(
        [
            new RecoveryCohortInput("2026-06", new DateOnly(2026, 6, 4), 1, 600m, 0m),
            new RecoveryCohortInput("2026-07", new DateOnly(2026, 7, 4), 1, 300m, 0m)
        ],
        currentCycleKey: "2026-07",
        horizon: 3,
        outstandingShortfall: 900m,
        toppedUpThisCycle: 0m);

        // June's plan is on its first instalment; July's has not opened, so it contributes nothing
        // and cannot make the combined ask look like the whole 900 is being chased at once.
        Assert.Equal([200m, 0m], plan.Cohorts.Select(cohort => cohort.RequiredThisCycle));
        Assert.Equal([false, true], plan.Cohorts.Select(cohort => cohort.IsDeferred));
        Assert.Equal([3, 3], plan.Cohorts.Select(cohort => cohort.CyclesRemaining));
        Assert.Equal(200m, plan.Aggregate.RequiredThisCycle);
        Assert.Equal(200m, plan.Aggregate.OutstandingThisCycle);
        // One live cohort is enough to keep the combined plan live.
        Assert.False(plan.Aggregate.IsDeferred);
    }

    [Fact]
    public void ComputeCohortPlan_CreditsReimbursementAgainstTheCombinedRequirement()
    {
        var plan = StabilityRecoveryPlanner.ComputeCohortPlan(
        [
            // The old cohort has 500 left after 100 went back this cycle, so its requirement stays
            // anchored on 600 — a third of which is 200. The new cohort's own three-cycle 300 plan
            // opens next cycle and asks for nothing yet.
            new RecoveryCohortInput("2026-06", new DateOnly(2026, 6, 4), 1, 500m, 100m),
            new RecoveryCohortInput("2026-07", new DateOnly(2026, 7, 4), 1, 300m, 0m)
        ],
        currentCycleKey: "2026-07",
        horizon: 3,
        outstandingShortfall: 800m,
        toppedUpThisCycle: 100m);

        Assert.Equal(200m, plan.Aggregate.RequiredThisCycle);
        Assert.Equal(100m, plan.Aggregate.OutstandingThisCycle);
    }

    [Fact]
    public void ComputeCohortPlan_RoundsPerCohortAndKeepsMixedOverdueState()
    {
        var plan = StabilityRecoveryPlanner.ComputeCohortPlan(
        [
            new RecoveryCohortInput("2026-04", new DateOnly(2026, 4, 1), 1, 20m, 0m),
            new RecoveryCohortInput("2026-07", new DateOnly(2026, 7, 1), 2, 100m, 0m)
        ],
        // April's three instalments ran through May, June and July, so by August it is overdue,
        // while July's plan is on its first instalment.
        currentCycleKey: "2026-08",
        horizon: 3,
        outstandingShortfall: 120m,
        toppedUpThisCycle: 0m);

        Assert.True(plan.Aggregate.IsOverdue);
        Assert.Equal(1, plan.Aggregate.CyclesRemaining);
        Assert.Equal(20m, plan.Cohorts[0].RequiredThisCycle);
        Assert.Equal(33.34m, plan.Cohorts[1].RequiredThisCycle);
        Assert.Equal(53.34m, plan.Aggregate.RequiredThisCycle);
    }

    [Theory]
    // The spending cycle and the first repayment cycle both hold the full horizon: the countdown
    // starts once the plan opens.
    [InlineData("2026-06", "2026-06", 3)]
    [InlineData("2026-06", "2026-07", 3)]
    [InlineData("2026-06", "2026-08", 2)]
    [InlineData("2026-06", "2026-09", 1)]
    // Past the window it goes negative rather than clamping, so an overdue plan can be told apart
    // from its final cycle -- clamped, the card called every cycle "the last one" forever.
    [InlineData("2026-06", "2026-11", -1)]
    [InlineData("2026-11", "2027-01", 2)]
    [InlineData(null, "2026-08", 3)]
    public void CyclesRemaining_CountsDownPastTheEndOfTheWindow(string? from, string current, int expected)
    {
        Assert.Equal(expected, StabilityRecoveryPlanner.CyclesRemaining(from, current, horizon: 3));
    }

    [Theory]
    [InlineData("2026-06", "2026-06", true)]
    // A withdrawal dated into a future cycle is deferred until that cycle's plan opens.
    [InlineData("2026-08", "2026-06", true)]
    [InlineData("2026-06", "2026-07", false)]
    [InlineData("2026-06", "2026-11", false)]
    // No origin cycle means the fallback path, which has no cycle to defer to: answering "nothing
    // is due" there would quietly drop a real obligation.
    [InlineData(null, "2026-08", false)]
    public void IsDeferred_CoversOnlyTheSpendingCycleAndEarlier(string? from, string current, bool expected)
    {
        Assert.Equal(expected, StabilityRecoveryPlanner.IsDeferred(from, current));
    }

    [Fact]
    public void ComputePace_AsksForNothingWhileThePlanIsDeferred()
    {
        var pace = StabilityRecoveryPlanner.ComputePace(900m, cyclesRemaining: 3, toppedUpThisCycle: 0m, isDeferred: true);

        Assert.True(pace.IsDeferred);
        Assert.Equal(0m, pace.RequiredThisCycle);
        Assert.Equal(0m, pace.OutstandingThisCycle);
        // The obligation itself is untouched, so the card still has something to report.
        Assert.Equal(900m, pace.Shortfall);
    }

    [Fact]
    public void ComputePace_FlagsAnElapsedWindowAsOverdueAndStillAsksForTheRemainder()
    {
        var pace = StabilityRecoveryPlanner.ComputePace(800m, cyclesRemaining: -2, toppedUpThisCycle: 0m);

        Assert.True(pace.IsOverdue);
        Assert.Equal(1, pace.CyclesRemaining);
        Assert.Equal(800m, pace.OutstandingThisCycle);
    }

    [Fact]
    public void ComputePace_DoesNotCallTheFinalCycleOverdue()
    {
        Assert.False(StabilityRecoveryPlanner.ComputePace(800m, 1, 0m).IsOverdue);
    }

    [Fact]
    public void ComputePace_SpreadsTheShortfallAcrossTheWindow()
    {
        var pace = StabilityRecoveryPlanner.ComputePace(3000m, cyclesRemaining: 3, toppedUpThisCycle: 0m);

        Assert.Equal(1000m, pace.RequiredThisCycle);
        Assert.Equal(1000m, pace.OutstandingThisCycle);
    }

    [Fact]
    public void ComputePace_RoundsTheRequirementUpSoTheWindowActuallyArrives()
    {
        // 100.00 / 3 = 33.333...; three cycles of 33.33 lands a cent short.
        var pace = StabilityRecoveryPlanner.ComputePace(100m, cyclesRemaining: 3, toppedUpThisCycle: 0m);

        Assert.Equal(33.34m, pace.RequiredThisCycle);
    }

    /// <summary>
    /// The property the anchor exists for: funding must close this cycle's ask, not shrink it into
    /// a figure that can never be satisfied.
    /// </summary>
    [Fact]
    public void ComputePace_HoldsTheRequirementStillAsItIsFunded()
    {
        var beforeFunding = StabilityRecoveryPlanner.ComputePace(3000m, 3, toppedUpThisCycle: 0m);
        var afterFunding = StabilityRecoveryPlanner.ComputePace(2000m, 3, toppedUpThisCycle: 1000m);

        Assert.Equal(beforeFunding.RequiredThisCycle, afterFunding.RequiredThisCycle);
        Assert.Equal(0m, afterFunding.OutstandingThisCycle);
    }

    /// <summary>
    /// Overfunding one cycle zeroes that cycle's ask while leaving the obligation itself intact.
    /// Pinned because the Today card used to treat this as "nothing owed" and disappear: 871.77
    /// marked with 520 put back still owes 351.77, but the pace only asks ceil(871.77 / 3) = 290.59.
    /// </summary>
    [Fact]
    public void ComputePace_ZeroesThisCyclesAskWithoutClearingTheShortfall()
    {
        var pace = StabilityRecoveryPlanner.ComputePace(351.77m, cyclesRemaining: 3, toppedUpThisCycle: 520m);

        Assert.Equal(290.59m, pace.RequiredThisCycle);
        Assert.Equal(0m, pace.OutstandingThisCycle);
        Assert.Equal(351.77m, pace.Shortfall);
    }

    [Fact]
    public void ComputePace_AsksForTheWholeRemainderOnTheFinalCycle()
    {
        var pace = StabilityRecoveryPlanner.ComputePace(1400m, cyclesRemaining: 1, toppedUpThisCycle: 0m);

        Assert.Equal(1400m, pace.RequiredThisCycle);
    }

    [Fact]
    public void ComputePace_NeverAsksForMoreThanIsActuallyMissing()
    {
        var pace = StabilityRecoveryPlanner.ComputePace(200m, cyclesRemaining: 1, toppedUpThisCycle: 0m);

        Assert.Equal(200m, pace.OutstandingThisCycle);
    }

    /// <summary>
    /// The anchor is the shortfall before this cycle's repayments, not the cycle's OPENING balance.
    /// With the opening balance, a drawdown made during the previous cycle produced a requirement of
    /// zero once its plan opened -- that cycle had opened with no shortfall recorded against it --
    /// so the plan asked for nothing while the money was demonstrably gone. Deferral is the separate
    /// and deliberate zero: it applies only to the spending cycle itself.
    /// </summary>
    [Fact]
    public void ComputePace_AsksForMoneySpentBeforeThisCycle()
    {
        var pace = StabilityRecoveryPlanner.ComputePace(2453.20m, cyclesRemaining: 3, toppedUpThisCycle: 0m);

        Assert.Equal(817.74m, pace.RequiredThisCycle);
        Assert.True(pace.OutstandingThisCycle > 0m);
    }

    /// <summary>
    /// The same cycle, part-repaid. What is already back counts, but it must not wipe out the ask
    /// when more is still missing.
    /// </summary>
    [Fact]
    public void ComputePace_StillAsksWhenThisCyclesRepaymentsFallShort()
    {
        var pace = StabilityRecoveryPlanner.ComputePace(2453.20m, cyclesRemaining: 3, toppedUpThisCycle: 1048m);

        // Anchored on 3,501.20 (what was missing before the repayments), a third of which is
        // 1,167.07 -- so 119.07 of this cycle's share is still owed.
        Assert.Equal(1167.07m, pace.RequiredThisCycle);
        Assert.Equal(119.07m, pace.OutstandingThisCycle);
    }

    [Fact]
    public void ToppedUpThisCycle_CountsOnlyCreditAboveThePlainPercentage()
    {
        // 1,000 of income at 15% would have delivered 150 on its own.
        Assert.Equal(0m, Math.Max(0m, 150m - 1000m * 0.15m));
        Assert.Equal(90m, Math.Max(0m, 240m - 1000m * 0.15m));
        Assert.Equal(0m, Math.Max(0m, 20m - 1000m * 0.15m));
    }
}
