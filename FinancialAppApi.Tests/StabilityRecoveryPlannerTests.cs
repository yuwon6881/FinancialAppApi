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
    public void ProposeTopUp_SplitsTheDrawAcrossTheThreeBucketsInProportion()
    {
        var offer = StabilityRecoveryPlanner.ProposeTopUp(Pace(170m), incomeAmount: 1000m, Buckets());

        Assert.Equal(170m, offer.ProposedTopUp);
        Assert.False(offer.IsReduced);
        Assert.Equal(100m, Draw(offer, "Essentials"));
        Assert.Equal(50m, Draw(offer, "Growth"));
        Assert.Equal(20m, Draw(offer, "Rewards"));
        Assert.Equal(offer.ProposedTopUp, offer.Draws.Sum(draw => draw.Amount));
    }

    [Fact]
    public void ProposeTopUp_DoesNotLetTheNormalShareReduceTheReloadBelowTarget()
    {
        var pace = new RecoveryPace(100m, 3, 100m, 0m, 100m, false);

        var offer = StabilityRecoveryPlanner.ProposeTopUp(
            pace, 1000m, Buckets(), stabilityAlloc: 0.15m);

        Assert.Equal(100m, offer.ProposedTopUp);
    }

    [Fact]
    public void ProposeTopUp_CapsExtraAtTheExplicitShortfall()
    {
        var pace = new RecoveryPace(200m, 3, 200m, 0m, 200m, false);

        var offer = StabilityRecoveryPlanner.ProposeTopUp(
            pace, 1000m, Buckets(), stabilityAlloc: 0.15m);

        Assert.Equal(200m, offer.ProposedTopUp);
    }

    [Fact]
    public void ProposeTopUp_OffersNothingWhenTheNormalShareActuallyReachesTarget()
    {
        var offer = StabilityRecoveryPlanner.ProposeTopUp(
            Pace(100m), incomeAmount: 1000m, Buckets(), stabilityAlloc: 0.15m,
            currentBalance: 9900m, target: 10000m);

        Assert.Equal(0m, offer.ProposedTopUp);
    }

    [Fact]
    public void ProposeTopUp_CannotDrawMoreThanTheThreeBucketsReceive()
    {
        // 100 of income only sends 85 to the other three, however much the pace wants.
        var offer = StabilityRecoveryPlanner.ProposeTopUp(Pace(1000m), incomeAmount: 100m, Buckets());

        Assert.Equal(85m, offer.ProposedTopUp);
    }

    /// <summary>Bills come first: a buffer refilled with the rent is not a buffer.</summary>
    [Fact]
    public void ProposeTopUp_HoldsTheDrawBackSoEssentialsStillCoversItsBills()
    {
        var buckets = Buckets(essentialsBalance: 200m, essentialsCommitted: 600m);

        var offer = StabilityRecoveryPlanner.ProposeTopUp(Pace(500m), incomeAmount: 1000m, buckets);

        // Essentials holds 200 and receives 500, so 100 is spare; at a 10/17 weight that caps the
        // whole draw at 170.
        Assert.Equal(170m, offer.ProposedTopUp);
        Assert.True(offer.IsReduced);
        Assert.Equal("Essentials", offer.LimitedBy);
        Assert.Equal(100m, Draw(offer, "Essentials"));
    }

    /// <summary>
    /// Savings goals are earmarks against Rewards, so the same floor protects them -- otherwise two
    /// features quietly claim the same money and the deadline-bound one loses.
    /// </summary>
    [Fact]
    public void ProposeTopUp_HoldsTheDrawBackSoSavingsGoalsStillGetTheirCycle()
    {
        var buckets = Buckets(rewardsBalance: 0m, rewardsCommitted: 90m);

        var offer = StabilityRecoveryPlanner.ProposeTopUp(Pace(500m), incomeAmount: 1000m, buckets);

        // Rewards receives 100 against 90 of goal funding, leaving 10 at a 2/17 weight.
        Assert.Equal(85m, offer.ProposedTopUp);
        Assert.Equal("Rewards", offer.LimitedBy);
    }

    [Fact]
    public void ProposeTopUp_OffersNothingWhenEveryPennyIsAlreadyPromised()
    {
        var buckets = Buckets(essentialsBalance: 0m, essentialsCommitted: 500m);

        var offer = StabilityRecoveryPlanner.ProposeTopUp(Pace(500m), incomeAmount: 1000m, buckets);

        Assert.Equal(0m, offer.ProposedTopUp);
        Assert.True(offer.IsReduced);
        Assert.Empty(offer.Draws);
    }

    [Fact]
    public void ProposeTopUp_RoundsTheOfferDownSoACapIsNeverExceeded()
    {
        var buckets = Buckets(essentialsBalance: 0m, essentialsCommitted: 499.995m);

        var offer = StabilityRecoveryPlanner.ProposeTopUp(Pace(500m), incomeAmount: 1000m, buckets);

        // 0.005 of headroom at a 10/17 weight is 0.0085 -- floored to nothing rather than up to a cent.
        Assert.Equal(0m, offer.ProposedTopUp);
    }

    [Fact]
    public void ProposeTopUp_IgnoresABucketSetToZeroPercentWithoutDividingByZero()
    {
        var buckets = new[]
        {
            new BucketState("Essentials", 0.60m, 1000m, 0m),
            new BucketState("Growth", 0m, 1000m, 0m),
            new BucketState("Rewards", 0.25m, 1000m, 0m)
        };

        var offer = StabilityRecoveryPlanner.ProposeTopUp(Pace(200m), incomeAmount: 1000m, buckets);

        Assert.Equal(200m, offer.ProposedTopUp);
        Assert.DoesNotContain(offer.Draws, draw => draw.Bucket == "Growth");
    }

    [Fact]
    public void ProposeTopUp_OffersNothingWhenTheFundTakesEverything()
    {
        var buckets = new[]
        {
            new BucketState("Essentials", 0m, 0m, 0m),
            new BucketState("Growth", 0m, 0m, 0m),
            new BucketState("Rewards", 0m, 0m, 0m)
        };

        Assert.Equal(0m, StabilityRecoveryPlanner.ProposeTopUp(Pace(200m), 1000m, buckets).ProposedTopUp);
    }

    [Fact]
    public void ProposeTopUp_OffersNothingWhenThisCycleIsAlreadySettled()
    {
        Assert.Equal(0m, StabilityRecoveryPlanner.ProposeTopUp(Pace(0m), 1000m, Buckets()).ProposedTopUp);
    }

    [Fact]
    public void ProposeTopUp_KeepsTheDrawsSummingToTheOfferThroughRounding()
    {
        var offer = StabilityRecoveryPlanner.ProposeTopUp(Pace(100.03m), incomeAmount: 1000m, Buckets());

        Assert.Equal(offer.ProposedTopUp, offer.Draws.Sum(draw => draw.Amount));
    }

    [Fact]
    public void ToppedUpThisCycle_CountsOnlyCreditAboveThePlainPercentage()
    {
        // 1,000 of income at 15% would have delivered 150 on its own.
        Assert.Equal(0m, StabilityRecoveryPlanner.ToppedUpThisCycle(150m, 1000m, 0.15m));
        Assert.Equal(90m, StabilityRecoveryPlanner.ToppedUpThisCycle(240m, 1000m, 0.15m));
        Assert.Equal(0m, StabilityRecoveryPlanner.ToppedUpThisCycle(20m, 1000m, 0.15m));
    }

    private static RecoveryPace Pace(decimal outstandingThisCycle)
    {
        return new RecoveryPace(3000m, 3, outstandingThisCycle, 0m, outstandingThisCycle, false);
    }

    private static IReadOnlyList<BucketState> Buckets(
        decimal essentialsBalance = 5000m,
        decimal essentialsCommitted = 0m,
        decimal rewardsBalance = 5000m,
        decimal rewardsCommitted = 0m)
    {
        return new[]
        {
            new BucketState("Essentials", 0.50m, essentialsBalance, essentialsCommitted),
            new BucketState("Growth", 0.25m, 5000m, 0m),
            new BucketState("Rewards", 0.10m, rewardsBalance, rewardsCommitted)
        };
    }

    private static decimal Draw(RecoveryOffer offer, string bucket)
    {
        return offer.Draws.Single(draw => draw.Bucket == bucket).Amount;
    }
}
