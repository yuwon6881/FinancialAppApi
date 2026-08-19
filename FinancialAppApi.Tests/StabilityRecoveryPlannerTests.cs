using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Tests;

/// <summary>
/// The emergency-fund recovery math. The per-cycle requirement is anchored on the outstanding
/// obligation before this cycle's repayments, so it holds still while being funded.
/// </summary>
public class StabilityRecoveryPlannerTests
{
    [Theory]
    [InlineData("2026-06", "2026-06", 3)]
    [InlineData("2026-06", "2026-07", 2)]
    [InlineData("2026-06", "2026-08", 1)]
    // Past the window it goes negative rather than clamping, so an overdue plan can be told apart
    // from its final cycle -- clamped, the card called every cycle "the last one" forever.
    [InlineData("2026-06", "2026-11", -2)]
    [InlineData("2026-11", "2027-01", 1)]
    [InlineData(null, "2026-08", 3)]
    public void CyclesRemaining_CountsDownPastTheEndOfTheWindow(string? from, string current, int expected)
    {
        Assert.Equal(expected, StabilityRecoveryPlanner.CyclesRemaining(from, current, horizon: 3));
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
    /// The regression that hid the card in the one cycle it mattered. Anchoring the requirement on
    /// the cycle's OPENING balance meant a drawdown made during the current cycle produced a
    /// requirement of zero -- the cycle had opened with no shortfall at all -- so the plan asked for
    /// nothing while the money was demonstrably gone.
    /// </summary>
    [Fact]
    public void ComputePace_AsksForMoneySpentDuringTheCurrentCycle()
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
