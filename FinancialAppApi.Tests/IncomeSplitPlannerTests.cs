using FinancialAppApi.Models;
using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Tests;

/// <summary>
/// The cap and the overflow redirect used to live only on the client, which meant any caller that
/// was not the web form -- an AI ledger draft, a recurring settlement, an outbox replay whose
/// balance had gone stale -- applied raw percentages and sailed past the target. These pin the
/// server-side behaviour that replaced it.
/// </summary>
public class IncomeSplitPlannerTests
{
    private const decimal Ess = 0.50m;
    private const decimal Gro = 0.25m;
    private const decimal Sta = 0.15m;
    private const decimal Rew = 0.10m;

    [Theory]
    [InlineData(StabilityOverflowRedirectOptions.EssentialsOnly, "Essentials")]
    [InlineData(StabilityOverflowRedirectOptions.GrowthOnly, "Growth")]
    [InlineData(StabilityOverflowRedirectOptions.RewardsOnly, "Rewards")]
    public void ResolveRedirectTargets_SendsTheWholeShareToASingleBucket(string redirect, string expected)
    {
        var targets = IncomeSplitPlanner.ResolveRedirectTargets(redirect);

        var target = Assert.Single(targets);
        Assert.Equal(expected, target.Bucket);
        Assert.Equal(1m, target.Weight);
    }

    /// <summary>
    /// The regression that motivated the parser: the client matched only the three "X 100%" values,
    /// so both Essentials splits silently redirected to Growth+Rewards instead.
    /// </summary>
    [Theory]
    [InlineData(StabilityOverflowRedirectOptions.EssentialsGrowth, "Essentials", "Growth")]
    [InlineData(StabilityOverflowRedirectOptions.EssentialsRewards, "Essentials", "Rewards")]
    [InlineData(StabilityOverflowRedirectOptions.GrowthRewards, "Growth", "Rewards")]
    public void ResolveRedirectTargets_SplitsEvenlyAcrossBothNamedBuckets(string redirect, string first, string second)
    {
        var targets = IncomeSplitPlanner.ResolveRedirectTargets(redirect);

        Assert.Equal(2, targets.Count);
        Assert.Equal(first, targets[0].Bucket);
        Assert.Equal(second, targets[1].Bucket);
        Assert.All(targets, target => Assert.Equal(0.5m, target.Weight));
    }

    [Fact]
    public void ResolveRedirectTargets_CoversEveryOptionTheSettingsScreenOffers()
    {
        // A new option added to the select without a parser case would fall back silently, which is
        // exactly how the Essentials splits went unnoticed.
        Assert.All(
            StabilityOverflowRedirectOptions.All,
            option => Assert.Equal(
                1m,
                IncomeSplitPlanner.ResolveRedirectTargets(option).Sum(target => target.Weight)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something nobody wrote")]
    public void ResolveRedirectTargets_FallsBackToGrowthAndRewards(string? redirect)
    {
        var targets = IncomeSplitPlanner.ResolveRedirectTargets(redirect);

        Assert.Equal(new[] { "Growth", "Rewards" }, targets.Select(target => target.Bucket));
    }

    [Fact]
    public void ResolveRedirectTargets_HonoursUnevenWeights()
    {
        var targets = IncomeSplitPlanner.ResolveRedirectTargets("Split: Growth 75%, Rewards 25%");

        Assert.Equal(0.75m, targets[0].Weight);
        Assert.Equal(0.25m, targets[1].Weight);
    }

    [Fact]
    public void Resolve_LeavesTheConfiguredSplitAloneWhenTheFundHasRoom()
    {
        var spec = Resolve(amount: 1000m, stabilityBalance: 0m, stabilityTarget: 10000m);

        Assert.Equal(Ess, spec.Essentials);
        Assert.Equal(Gro, spec.Growth);
        Assert.Equal(Sta, spec.Stability);
        Assert.Equal(Rew, spec.Rewards);
    }

    [Fact]
    public void Resolve_StopsAtTheTargetAndRedirectsTheRest()
    {
        // 1,000 of income would normally send 150 to the fund, but only 60 of room is left.
        var spec = Resolve(amount: 1000m, stabilityBalance: 9940m, stabilityTarget: 10000m);

        Assert.Equal(0.06m, spec.Stability);
        // The 90 that could not fit splits evenly between Growth and Rewards by default.
        Assert.Equal(Gro + 0.045m, spec.Growth);
        Assert.Equal(Rew + 0.045m, spec.Rewards);
        Assert.Equal(Ess, spec.Essentials);
        Assert.Equal(1m, spec.Total);
    }

    [Fact]
    public void Resolve_SendsNothingToAFundAlreadyAtTarget()
    {
        var spec = Resolve(amount: 1000m, stabilityBalance: 12000m, stabilityTarget: 10000m);

        Assert.Equal(0m, spec.Stability);
        Assert.Equal(1m, spec.Total);
    }

    [Fact]
    public void Resolve_AddsARecoveryTopUpOnTopOfTheUsualShare()
    {
        // 90 on top of the usual 150 -> 24% of a 1,000 salary.
        var spec = Resolve(amount: 1000m, stabilityBalance: 5000m, stabilityTarget: 10000m, requestedTopUp: 90m);

        Assert.Equal(0.24m, spec.Stability);
        Assert.Equal(1m, spec.Total);
    }

    [Fact]
    public void Resolve_DrawsTheTopUpFromTheOtherThreeInProportion()
    {
        var spec = Resolve(amount: 1000m, stabilityBalance: 5000m, stabilityTarget: 10000m, requestedTopUp: 85m);

        // 85 comes out of the 850 the other three were due, i.e. each gives up a tenth of its share.
        Assert.Equal(Ess * 0.9m, spec.Essentials);
        Assert.Equal(Gro * 0.9m, spec.Growth);
        Assert.Equal(Rew * 0.9m, spec.Rewards);
        Assert.Equal(1m, spec.Total);
    }

    [Fact]
    public void Resolve_ClampsATopUpThatWouldOvershootTheTarget()
    {
        // Only 200 of room, but the caller asked for 150 + 400 on top.
        var spec = Resolve(amount: 1000m, stabilityBalance: 9800m, stabilityTarget: 10000m, requestedTopUp: 400m);

        Assert.Equal(0.2m, spec.Stability);
        Assert.Equal(1m, spec.Total);
    }

    [Fact]
    public void ClampProposed_LeavesALegalClientProposalUntouched()
    {
        var proposed = new IncomeSplitSpec(0.45m, 0.225m, 0.235m, 0.09m);

        var (spec, wasClamped) = IncomeSplitPlanner.ClampProposed(
            proposed, 1000m, stabilityBalance: 5000m, stabilityTarget: 10000m,
            StabilityOverflowRedirectOptions.GrowthRewards);

        Assert.False(wasClamped);
        Assert.Equal(0.235m, spec.Stability);
    }

    /// <summary>
    /// The offline case: the client proposed against a balance that had moved by the time the queue
    /// drained. Clamped rather than rejected, because the user cannot re-enter a replayed salary.
    /// </summary>
    [Fact]
    public void ClampProposed_ClampsAStaleProposalRatherThanRejectingIt()
    {
        var proposed = new IncomeSplitSpec(0.40m, 0.20m, 0.32m, 0.08m);

        var (spec, wasClamped) = IncomeSplitPlanner.ClampProposed(
            proposed, 1000m, stabilityBalance: 9900m, stabilityTarget: 10000m,
            StabilityOverflowRedirectOptions.RewardsOnly);

        Assert.True(wasClamped);
        Assert.Equal(0.1m, spec.Stability);
        // Every displaced cent lands where the setting points, never dropped.
        Assert.Equal(0.08m + 0.22m, spec.Rewards);
        Assert.Equal(1m, spec.Total);
    }

    [Fact]
    public void ToSpecString_WritesInvariantPercentagesTheAttributionParserCanReadBack()
    {
        var spec = new IncomeSplitSpec(0.45m, 0.225m, 0.235m, 0.09m);

        Assert.Equal("45,22.5,23.5,9", spec.ToSpecString());
        Assert.True(IncomeSplitSpec.TryParseSpecString(spec.ToSpecString(), out var roundTripped));
        Assert.Equal(spec, roundTripped);
    }

    [Theory]
    [InlineData("50,25,15")]
    [InlineData("50,25,15,-10")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseSpecString_RejectsMalformedSpecs(string? spec)
    {
        Assert.False(IncomeSplitSpec.TryParseSpecString(spec, out _));
    }

    private static IncomeSplitSpec Resolve(
        decimal amount,
        decimal stabilityBalance,
        decimal stabilityTarget,
        decimal requestedTopUp = 0m,
        string redirect = StabilityOverflowRedirectOptions.GrowthRewards)
    {
        return IncomeSplitPlanner.Resolve(
            amount, Ess, Gro, Sta, Rew, stabilityBalance, stabilityTarget, redirect, requestedTopUp);
    }
}
