using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public class FinancialClockTests
{
    [Fact]
    public void Today_UsesConfiguredFinancialTimezoneInsteadOfServerTimezone()
    {
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 7, 15, 16, 30, 0, TimeSpan.Zero));
        var configuration = TestHelpers.NewConfiguration(("Financial:TimeZoneId", "Asia/Kuala_Lumpur"));

        var clock = new FinancialClock(configuration, timeProvider);

        Assert.Equal(new DateOnly(2026, 7, 16), clock.Today);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
