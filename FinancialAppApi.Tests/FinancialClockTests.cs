using FinancialAppApi.Services;
using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Tests;

public class FinancialClockTests
{
    [Fact]
    public void StabilityPlanPoints_UseTheFinancialCycleBoundary()
    {
        var configuration = TestHelpers.NewConfiguration(("Financial:TimeZoneId", "Asia/Kuala_Lumpur"));
        var clock = new FinancialClock(configuration);
        var firstCycle = new DateTime(2026, 2, 28);
        var secondCycle = new DateTime(2026, 3, 28);
        StabilityPlanSnapshot[] revisions =
        [
            new(DateTime.UnixEpoch, 1_000m, 0.10m),
            new(new DateTime(2026, 3, 27, 17, 0, 0, DateTimeKind.Utc), 500m, 0.10m),
        ];

        var first = StabilityPlanRevisionService.PointsForCycle(revisions, firstCycle, secondCycle.AddDays(-1), clock);
        var second = StabilityPlanRevisionService.PointsForCycle(revisions, secondCycle, new DateTime(2026, 4, 27), clock);

        Assert.Single(first);
        Assert.Equal(revisions.Select(plan => plan.EffectiveAt), second.Select(point => point.EffectiveAt));
    }

    [Fact]
    public void Today_UsesConfiguredFinancialTimezoneInsteadOfServerTimezone()
    {
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 7, 15, 16, 30, 0, TimeSpan.Zero));
        var configuration = TestHelpers.NewConfiguration(("Financial:TimeZoneId", "Asia/Kuala_Lumpur"));

        var clock = new FinancialClock(configuration, timeProvider);

        Assert.Equal(new DateOnly(2026, 7, 16), clock.Today);
    }

    [Fact]
    public void ResolveTimeZone_FallsBackToUtcPlusEightWhenChiseledImageHasNoTzData()
    {
        var zone = FinancialClock.ResolveTimeZone(
            "Asia/Kuala_Lumpur",
            _ => throw new TimeZoneNotFoundException("tzdata is unavailable"));

        Assert.Equal(TimeSpan.FromHours(8), zone.BaseUtcOffset);
        Assert.False(zone.SupportsDaylightSavingTime);
    }

    [Fact]
    public void ResolveTimeZone_DoesNotHideAnUnknownConfiguredZone()
    {
        Assert.Throws<TimeZoneNotFoundException>(() => FinancialClock.ResolveTimeZone(
            "Invalid/FinancialZone",
            _ => throw new TimeZoneNotFoundException("unknown zone")));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
