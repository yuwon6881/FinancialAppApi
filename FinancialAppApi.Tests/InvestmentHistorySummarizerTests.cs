using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

/// <summary>
/// The long-range history is derived from month-end snapshots alone: these pin the time-weighted
/// chaining, the calendar-year and lifetime totals, the per-market split, and that an unknown
/// month leaves every period containing it unknown instead of zero.
/// </summary>
public sealed class InvestmentHistorySummarizerTests
{
    [Fact]
    public void ChainsMonthlyReturnsIntoYearsAndTheLifetime()
    {
        var rows = new List<InvestmentPeriodSnapshot>
        {
            // Dec 2030: 1,000 arrives mid-month (weighted half) and grows by 10.
            Total(2030, 12, opening: 0, closing: 1010, contributions: 1000, weighted: 500),
            // Jan 2031: no flows, +10%.
            Total(2031, 1, opening: 1010, closing: 1111, contributions: 0, weighted: 0),
            // Feb 2031: 500 added on day one (fully weighted), flat.
            Total(2031, 2, opening: 1111, closing: 1611, contributions: 500, weighted: 500),
        };

        var history = InvestmentHistorySummarizer.Summarize("sgd", rows);

        Assert.Equal("SGD", history.Currency);
        Assert.Equal([2031, 2030], history.Years.Select(year => year.Year).ToList());
        var y2030 = history.Years[1];
        Assert.True(y2030.IsFullYear);
        Assert.Equal(2m, y2030.ReturnPercent); // 10 / (0 + 500)
        Assert.Equal(10m, y2030.Growth);
        var y2031 = history.Years[0];
        Assert.False(y2031.IsFullYear);
        Assert.Equal(new DateOnly(2031, 2, 28), y2031.PeriodEnd);
        Assert.Equal(1010m, y2031.OpeningValue);
        Assert.Equal(1611m, y2031.ClosingValue);
        Assert.Equal(500m, y2031.NetContributions);
        Assert.Equal(101m, y2031.Growth);
        Assert.Equal(10m, y2031.ReturnPercent);

        Assert.Equal(1500m, history.Lifetime.NetContributions);
        Assert.Equal(1611m, history.Lifetime.Value);
        Assert.Equal(111m, history.Lifetime.Growth);
        Assert.Equal(12.2m, history.Lifetime.CumulativeReturnPercent); // 1.02 * 1.10 - 1
        Assert.Null(history.Lifetime.AnnualisedReturnPercent); // under a year is never extrapolated
        Assert.Equal([1000m, 1000m, 1500m], history.Months.Select(month => month.CumulativeContributions!.Value).ToList());
    }

    [Fact]
    public void AnnualisesOnlyOnceAFullYearExists()
    {
        var rows = new List<InvestmentPeriodSnapshot>();
        decimal value = 1000;
        for (var month = 1; month <= 24; month++)
        {
            var opening = month == 1 ? 0 : value;
            var contributions = month == 1 ? 1000m : 0m;
            var closing = month == 1 ? 1000m : value * 1.01m;
            rows.Add(Total(2030 + (month - 1) / 12, (month - 1) % 12 + 1, opening, closing, contributions, contributions));
            value = closing;
        }

        var history = InvestmentHistorySummarizer.Summarize("USD", rows);

        // 23 growth months at 1% over 24 months, annualised.
        var expected = (decimal)Math.Pow(Math.Pow(1.01, 23), 12d / 24) - 1;
        Assert.Equal(Math.Round(expected * 100, 3), Math.Round(history.Lifetime.AnnualisedReturnPercent!.Value, 3));
    }

    [Fact]
    public void AnUnknownMonthLeavesItsYearAndTheLifetimeUnknownButKeepsOtherYears()
    {
        var rows = new List<InvestmentPeriodSnapshot>
        {
            Total(2030, 12, opening: 0, closing: 1010, contributions: 1000, weighted: 500),
            Total(2031, 1, opening: 1010, closing: null, contributions: 0, weighted: 0,
                missing: "No price for VWRA on 2031-01-31."),
        };

        var history = InvestmentHistorySummarizer.Summarize("USD", rows);

        var y2031 = history.Years.Single(year => year.Year == 2031);
        Assert.Null(y2031.ReturnPercent);
        Assert.Null(y2031.Growth);
        Assert.Null(y2031.ClosingValue);
        Assert.False(y2031.IsComplete);
        Assert.Equal(["No price for VWRA on 2031-01-31."], y2031.MissingReasons);
        Assert.Equal(2m, history.Years.Single(year => year.Year == 2030).ReturnPercent);
        Assert.Null(history.Lifetime.CumulativeReturnPercent);
        Assert.False(history.Lifetime.IsComplete);
    }

    [Fact]
    public void SplitsGrowthByMarketFromTheMonthAMarketFirstHeldAnything()
    {
        var rows = new List<InvestmentPeriodSnapshot>
        {
            Total(2031, 1, opening: 0, closing: 1000, contributions: 1000, weighted: 1000),
            Segment(2031, 1, "USEquity", opening: 0, closing: 600, contributions: 600),
            Total(2031, 2, opening: 1000, closing: 1100, contributions: 0, weighted: 0),
            Segment(2031, 2, "USEquity", opening: 600, closing: 640, contributions: 0),
            Segment(2031, 2, "Bonds", opening: 0, closing: 410, contributions: 400),
        };

        var history = InvestmentHistorySummarizer.Summarize("USD", rows);

        var markets = history.Lifetime.Segments.ToDictionary(segment => segment.Segment);
        Assert.Equal(["Bonds", "USEquity"], markets.Keys.Order().ToList());
        Assert.Equal(400m, markets["Bonds"].NetContributions);
        Assert.Equal(10m, markets["Bonds"].Growth);
        Assert.Equal(5m, markets["Bonds"].ReturnPercent); // 10 / (400 / 2), Bonds idle in January
        Assert.Equal(40m, markets["USEquity"].Growth);
        Assert.Equal(600m, markets["USEquity"].NetContributions);
    }

    [Fact]
    public void MoneyArrivingOnTheLastDayFallsBackToTheMidMonthWeight()
    {
        var history = InvestmentHistorySummarizer.Summarize("USD",
            [Total(2031, 1, opening: 0, closing: 1002, contributions: 1000, weighted: 0)]);

        Assert.Equal(0.4m, history.Years[0].ReturnPercent); // 2 / (1000 / 2)
    }

    [Fact]
    public void NoSnapshotsIsAnEmptyHistoryNotZeroes()
    {
        var history = InvestmentHistorySummarizer.Summarize("USD", []);

        Assert.Empty(history.Years);
        Assert.Empty(history.Months);
        Assert.Null(history.Lifetime.Value);
        Assert.Null(history.Lifetime.NetContributions);
        Assert.Null(history.Lifetime.AsOf);
    }

    private static InvestmentPeriodSnapshot Total(
        int year, int month, decimal opening, decimal? closing, decimal contributions, decimal weighted, string? missing = null)
        => new()
        {
            PeriodEnd = InvestmentPeriodCalculator.MonthEnd(new DateOnly(year, month, 1)),
            Segment = InvestmentSnapshotSegments.Total,
            OpeningValue = opening,
            ClosingValue = closing,
            NetContributions = contributions,
            WeightedContributions = weighted,
            Dividends = 0,
            RealisedProfitLoss = 0,
            IsComplete = missing is null,
            MissingReason = missing,
        };

    private static InvestmentPeriodSnapshot Segment(int year, int month, string segment, decimal opening, decimal closing, decimal contributions)
        => new()
        {
            PeriodEnd = InvestmentPeriodCalculator.MonthEnd(new DateOnly(year, month, 1)),
            Segment = segment,
            OpeningValue = opening,
            ClosingValue = closing,
            NetContributions = contributions,
            WeightedContributions = contributions / 2,
            IsComplete = true,
        };
}
