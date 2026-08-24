using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Tests;

/// <summary>
/// The cache carries obligation identities across cycle boundaries. Without them the next cycle
/// replays against a single anonymous entry and cannot tell a partly-repaid drawdown from one
/// already put back in full, which is what let settled drawdowns keep inflating the reported ask.
/// </summary>
public class StabilityReloadObligationCacheTests
{
    [Fact]
    public void RoundTrip_PreservesFifoOrderIdentityAmountsAndDate()
    {
        var serialized = StabilityReloadObligationCache.Serialize(
        [
            // Both are on the same ledger date, but z-first was posted first. Sorting by id here
            // would reverse FIFO and make the next cycle settle the wrong transaction.
            new ReloadObligation("z-first", 100m, 40.55m, new DateOnly(2026, 7, 1)),
            new ReloadObligation("a-second", 80m, 80m, new DateOnly(2026, 7, 1))
        ]);

        var restored = StabilityReloadObligationCache.Deserialize(serialized)!;

        Assert.Equal(["z-first", "a-second"], restored.Select(item => item.TransactionId));
        Assert.Equal((100m, 40.55m), (restored[0].OriginalAmount, restored[0].RemainingAmount));
        Assert.Equal(new DateOnly(2026, 7, 1), restored[0].Date);
    }

    [Fact]
    public void Serialize_DropsSettledObligationsAndStoresNothingWhenAllAreSettled()
    {
        var mixed = StabilityReloadObligationCache.Serialize(
        [
            new ReloadObligation("settled", 100m, 0m, new DateOnly(2026, 7, 1)),
            new ReloadObligation("owing", 80m, 80m, new DateOnly(2026, 7, 2))
        ]);
        Assert.Equal(["owing"], StabilityReloadObligationCache.Deserialize(mixed)!
            .Select(item => item.TransactionId));

        Assert.Null(StabilityReloadObligationCache.Serialize(
            [new ReloadObligation("settled", 100m, 0m, new DateOnly(2026, 7, 1))]));
        Assert.Null(StabilityReloadObligationCache.Serialize(null));
    }

    /// <summary>
    /// Unreadable cached state must fall back to the aggregate rather than claim nothing is owed.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Deserialize_FailsClosedToNull(string? cached)
    {
        Assert.Null(StabilityReloadObligationCache.Deserialize(cached));
    }

    [Fact]
    public void OpeningState_KeepsTheAggregateAuthoritativeAndAddsIdentities()
    {
        var withIdentities = StabilityReloadObligationCache.OpeningState(
            180m,
            new DateOnly(2026, 6, 1),
            StabilityReloadObligationCache.Serialize(
                [new ReloadObligation("carried", 300m, 180m, new DateOnly(2026, 6, 1))]));

        Assert.Equal(180m, withIdentities.Outstanding);
        Assert.Equal(new DateOnly(2026, 6, 1), withIdentities.OldestOutstandingDate);
        Assert.Equal(300m, withIdentities.Obligations!.Single().OriginalAmount);

        var withoutIdentities = StabilityReloadObligationCache.OpeningState(
            180m, new DateOnly(2026, 6, 1), null);
        Assert.Equal(180m, withoutIdentities.Outstanding);
        Assert.Null(withoutIdentities.Obligations);
    }

    [Fact]
    public void OpeningState_DropsDetailThatDoesNotAccountForTheAuthoritativeAggregate()
    {
        var opening = StabilityReloadObligationCache.OpeningState(
            180m,
            new DateOnly(2026, 6, 1),
            StabilityReloadObligationCache.Serialize(
                [new ReloadObligation("stale", 500m, 300m, new DateOnly(2026, 6, 1))]));

        Assert.Equal(180m, opening.Outstanding);
        Assert.Null(opening.Obligations);

        var replay = StabilityReloadLedger.Replay(
            opening,
            openingBalance: 0m,
            target: 0m,
            [new ReloadMovement(new DateOnly(2026, 7, 5), 50m, 50m, false, "repayment")]);
        Assert.Equal(130m, replay.Outstanding);
    }

    [Fact]
    public void NextCycleRepayment_PreservesSameDayPostingOrder()
    {
        var opening = StabilityReloadObligationCache.OpeningState(
            180m,
            new DateOnly(2026, 6, 1),
            StabilityReloadObligationCache.Serialize(
            [
                new ReloadObligation("z-first", 100m, 100m, new DateOnly(2026, 6, 1)),
                new ReloadObligation("a-second", 80m, 80m, new DateOnly(2026, 6, 1))
            ]));

        var replay = StabilityReloadLedger.Replay(
            opening,
            openingBalance: 0m,
            target: 0m,
            [new ReloadMovement(new DateOnly(2026, 7, 5), 120m, 120m, false, "repayment")]);
        var byId = replay.Obligations!.ToDictionary(item => item.TransactionId);

        Assert.Equal(0m, byId["z-first"].RemainingAmount);
        Assert.Equal(60m, byId["a-second"].RemainingAmount);
        Assert.Equal(80m, replay.OpenMarkedTotal);
        Assert.Equal(20m, replay.OpenRepaidTotal);
    }

    /// <summary>
    /// A cached obligation that is still owing keeps its original amount, so the cycle after it is
    /// carried still reports the full drawdown and what has gone back against it -- not just the
    /// remainder as an apparently untouched obligation.
    /// </summary>
    [Fact]
    public void CarriedObligation_ReportsItsOriginalAmountAfterTheBoundary()
    {
        var opening = StabilityReloadObligationCache.OpeningState(
            180m,
            new DateOnly(2026, 6, 1),
            StabilityReloadObligationCache.Serialize(
                [new ReloadObligation("carried", 300m, 180m, new DateOnly(2026, 6, 1))]));

        var state = StabilityReloadLedger.Replay(
            opening,
            openingBalance: 0m,
            target: 0m,
            [new ReloadMovement(new DateOnly(2026, 7, 5), 50m, 50m, false, "repayment")]);

        Assert.Equal(130m, state.Outstanding);
        Assert.Equal(300m, state.OpenMarkedTotal);
        Assert.Equal(170m, state.OpenRepaidTotal);
        Assert.Equal(state.Outstanding, state.OpenMarkedTotal - state.OpenRepaidTotal);
    }
}
