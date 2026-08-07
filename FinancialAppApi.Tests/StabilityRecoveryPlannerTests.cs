using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Tests;

/// <summary>
/// The emergency-fund recovery math. The two rules worth defending are that recovery aims at the
/// fund's own high-water mark rather than the target (so it only ever asks back money that was
/// really in there), and that the per-cycle requirement is measured from the cycle's opening
/// balance (so it holds still while it is being funded).
/// </summary>
public class StabilityRecoveryPlannerTests
{
    [Fact]
    public void ComputeDrawdown_ReportsWhatWasSpentOutOfAFullyFundedPot()
    {
        var drawdown = StabilityRecoveryPlanner.ComputeDrawdown(
            highWaterMark: 10000m, target: 10000m, currentBalance: 7000m,
            balanceAtCycleStart: 7000m, lastDrawdownCycleKey: "2026-06", lastDrawdownAmount: 3000m);

        Assert.True(drawdown.IsActive);
        Assert.Equal(10000m, drawdown.RecoverableCeiling);
        Assert.Equal(3000m, drawdown.OutstandingShortfall);
    }

    /// <summary>
    /// Someone still filling the fund for the first time is at their own high-water mark, so there
    /// is nothing to "put back" -- the ordinary Stability share is already doing that job.
    /// </summary>
    [Fact]
    public void ComputeDrawdown_AsksNothingOfAFundThatHasOnlyEverGoneUp()
    {
        var drawdown = StabilityRecoveryPlanner.ComputeDrawdown(
            highWaterMark: 2000m, target: 10000m, currentBalance: 2000m,
            balanceAtCycleStart: 1800m, lastDrawdownCycleKey: null, lastDrawdownAmount: 0m);

        Assert.False(drawdown.IsActive);
        Assert.Equal(0m, drawdown.OutstandingShortfall);
    }

    [Fact]
    public void ComputeDrawdown_RaisesTheMarkToTheLiveBalanceAtAnAllTimeHigh()
    {
        var drawdown = StabilityRecoveryPlanner.ComputeDrawdown(
            highWaterMark: 4000m, target: 10000m, currentBalance: 4500m,
            balanceAtCycleStart: 4000m, lastDrawdownCycleKey: null, lastDrawdownAmount: 0m);

        Assert.Equal(4500m, drawdown.HighWaterMark);
        Assert.Equal(0m, drawdown.OutstandingShortfall);
    }

    /// <summary>
    /// Raising the target is a new savings ambition, not a drawdown. Without the min() against the
    /// high-water mark, moving the target from 5,000 to 20,000 would immediately claim the user had
    /// spent 15,000 they never had.
    /// </summary>
    [Fact]
    public void ComputeDrawdown_DoesNotInventADrawdownWhenTheTargetIsRaised()
    {
        var drawdown = StabilityRecoveryPlanner.ComputeDrawdown(
            highWaterMark: 5000m, target: 20000m, currentBalance: 5000m,
            balanceAtCycleStart: 5000m, lastDrawdownCycleKey: null, lastDrawdownAmount: 0m);

        Assert.Equal(5000m, drawdown.RecoverableCeiling);
        Assert.False(drawdown.IsActive);
    }

    [Fact]
    public void ComputeDrawdown_ClosesTheAskWhenTheTargetDropsBelowTheBalance()
    {
        var drawdown = StabilityRecoveryPlanner.ComputeDrawdown(
            highWaterMark: 10000m, target: 3000m, currentBalance: 7000m,
            balanceAtCycleStart: 7000m, lastDrawdownCycleKey: "2026-06", lastDrawdownAmount: 3000m);

        Assert.False(drawdown.IsActive);
    }

    [Theory]
    [InlineData("2026-06", "2026-06", 3)]
    [InlineData("2026-06", "2026-07", 2)]
    [InlineData("2026-06", "2026-08", 1)]
    [InlineData("2026-06", "2026-11", 1)]
    [InlineData("2026-11", "2027-01", 1)]
    [InlineData(null, "2026-08", 3)]
    public void CyclesRemaining_CountsDownAndNeverReachesZero(string? from, string current, int expected)
    {
        Assert.Equal(expected, StabilityRecoveryPlanner.CyclesRemaining(from, current, horizon: 3));
    }

    [Fact]
    public void ComputePace_SpreadsTheShortfallAcrossTheWindow()
    {
        var pace = StabilityRecoveryPlanner.ComputePace(Drawdown(3000m, 3000m), cyclesRemaining: 3, toppedUpThisCycle: 0m);

        Assert.Equal(1000m, pace.RequiredThisCycle);
        Assert.Equal(1000m, pace.OutstandingThisCycle);
    }

    [Fact]
    public void ComputePace_RoundsTheRequirementUpSoTheWindowActuallyArrives()
    {
        // 100.00 / 3 = 33.333...; three cycles of 33.33 lands a cent short.
        var pace = StabilityRecoveryPlanner.ComputePace(Drawdown(100m, 100m), cyclesRemaining: 3, toppedUpThisCycle: 0m);

        Assert.Equal(33.34m, pace.RequiredThisCycle);
    }

    /// <summary>
    /// The reason the pace reads the opening balance: funding it must close this cycle's ask, not
    /// shrink it into a figure that can never be satisfied.
    /// </summary>
    [Fact]
    public void ComputePace_HoldsTheRequirementStillAsItIsFunded()
    {
        var beforeFunding = StabilityRecoveryPlanner.ComputePace(Drawdown(3000m, 3000m), 3, toppedUpThisCycle: 0m);
        var afterFunding = StabilityRecoveryPlanner.ComputePace(Drawdown(2000m, 3000m), 3, toppedUpThisCycle: 1000m);

        Assert.Equal(beforeFunding.RequiredThisCycle, afterFunding.RequiredThisCycle);
        Assert.Equal(0m, afterFunding.OutstandingThisCycle);
    }

    [Fact]
    public void ComputePace_AsksForTheWholeRemainderOnTheFinalCycle()
    {
        var pace = StabilityRecoveryPlanner.ComputePace(Drawdown(1400m, 1400m), cyclesRemaining: 1, toppedUpThisCycle: 0m);

        Assert.Equal(1400m, pace.RequiredThisCycle);
    }

    [Fact]
    public void ComputePace_NeverAsksForMoreThanIsActuallyMissing()
    {
        // Opening shortfall of 3,000 spread over one cycle, but 2,800 is already back.
        var pace = StabilityRecoveryPlanner.ComputePace(Drawdown(200m, 3000m), cyclesRemaining: 1, toppedUpThisCycle: 0m);

        Assert.Equal(200m, pace.OutstandingThisCycle);
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

    private static StabilityDrawdown Drawdown(decimal outstanding, decimal atCycleStart)
    {
        return StabilityRecoveryPlanner.ComputeDrawdown(
            highWaterMark: 10000m, target: 10000m,
            currentBalance: 10000m - outstanding,
            balanceAtCycleStart: 10000m - atCycleStart,
            lastDrawdownCycleKey: "2026-06", lastDrawdownAmount: atCycleStart);
    }

    private static RecoveryPace Pace(decimal outstandingThisCycle)
    {
        return new RecoveryPace(3000m, 3, outstandingThisCycle, 0m, outstandingThisCycle);
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
