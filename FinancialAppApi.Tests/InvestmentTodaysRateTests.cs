using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

/// <summary>
/// Constant-currency reporting stands one rate in for "today". These pin what may stand in: the
/// freshest of the direct pair, its inverse and the USD cross, never one older than the lookback.
/// </summary>
public sealed class InvestmentTodaysRateTests
{
    // A Monday.
    private static readonly DateOnly Today = new(2031, 6, 30);

    private readonly MarketDataSeriesResolver _resolver = new(new MarketDataProviderDescriptor(
        "test", "Test data", true, MarketDataCapabilities.RequiredForActivation, new MarketDataQuotaPolicy(6, 750, 200)));

    private ResolvedMarketFx? Resolve(string native, string app, params FxRateBar[] bars)
        => _resolver.ResolveFx(native, app, Today, bars, InvestmentConstantCurrency.RateNeedFrom(Today));

    private static FxRateBar Bar(string from, string to, DateOnly date, decimal rate) => new()
    {
        Provider = "test", BaseCurrency = from, QuoteCurrency = to, MarketDate = date, Rate = rate,
    };

    [Fact]
    public void AFridayCloseStandsInOverTheWeekend()
    {
        var rate = Resolve("USD", "MYR", Bar("USD", "MYR", Today.AddDays(-3), 4.5m));

        Assert.Equal(4.5m, rate?.Rate);
        Assert.Equal(Today.AddDays(-3), rate?.Date);
    }

    [Fact]
    public void ARateFromBeforeAHolidayRunStillCountsInsideTheLookback()
    {
        var rate = Resolve("USD", "MYR", Bar("USD", "MYR", Today.AddDays(-InvestmentConstantCurrency.RateLookbackDays), 4.4m));

        Assert.Equal(4.4m, rate?.Rate);
    }

    [Fact]
    public void ARateOlderThanTheLookbackIsMissingNotPresentedAsToday()
    {
        var rate = Resolve("USD", "MYR", Bar("USD", "MYR", Today.AddDays(-InvestmentConstantCurrency.RateLookbackDays - 1), 4.2m));

        Assert.Null(rate);
    }

    [Fact]
    public void AStaleDirectPairNeverHidesACurrentCrossRate()
    {
        var rate = Resolve(
            "SGD", "MYR",
            Bar("SGD", "MYR", Today.AddDays(-30), 3.0m),
            Bar("SGD", "USD", Today.AddDays(-1), 0.75m),
            Bar("USD", "MYR", Today.AddDays(-1), 4.4m));

        Assert.Equal(0.75m * 4.4m, rate?.Rate);
        Assert.Contains("cross", rate?.Source);
    }

    [Fact]
    public void AnOlderDirectPairInsideTheLookbackStillLosesToAFresherCross()
    {
        var rate = Resolve(
            "SGD", "MYR",
            Bar("SGD", "MYR", Today.AddDays(-5), 3.0m),
            Bar("SGD", "USD", Today.AddDays(-1), 0.75m),
            Bar("USD", "MYR", Today, 4.4m));

        Assert.Equal(0.75m * 4.4m, rate?.Rate);
        Assert.Equal(Today.AddDays(-1), rate?.Date);
    }

    [Fact]
    public void ADirectPairAsFreshAsTheCrossIsPreferred()
    {
        var rate = Resolve(
            "SGD", "MYR",
            Bar("SGD", "MYR", Today.AddDays(-1), 3.3m),
            Bar("SGD", "USD", Today.AddDays(-1), 0.75m),
            Bar("USD", "MYR", Today.AddDays(-1), 4.4m));

        Assert.Equal(3.3m, rate?.Rate);
        Assert.Contains("direct", rate?.Source);
    }

    [Fact]
    public void ACrossWithOneStaleLegIsNotACurrentRate()
    {
        var rate = Resolve(
            "SGD", "MYR",
            Bar("SGD", "USD", Today.AddDays(-20), 0.75m),
            Bar("USD", "MYR", Today, 4.4m));

        Assert.Null(rate);
    }

    [Fact]
    public void AFresherInverseBeatsAnOlderDirectBar()
    {
        var rate = Resolve(
            "USD", "MYR",
            Bar("USD", "MYR", Today.AddDays(-6), 4.0m),
            Bar("MYR", "USD", Today, 0.25m));

        Assert.Equal(4m, rate?.Rate);
        Assert.Equal(Today, rate?.Date);
    }
}
