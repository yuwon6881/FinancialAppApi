using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Investments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

/// <summary>
/// A long chart range reads settled months from the month-end cache and values only the days
/// after the newest one. It must draw exactly what the full day-by-day replay draws on every
/// date both produce, so the cache is a speed-up and never a different answer.
/// </summary>
public sealed class InvestmentChartSnapshotTests
{
    private const decimal Rate = 1.35m;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly DateOnly Start = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-20);

    [Fact]
    public async Task ALongRangeFromTheCacheMatchesTheFullReplayOnEveryDate()
    {
        await using var cached = TestHelpers.NewInMemoryContext();
        await using var replayed = TestHelpers.NewInMemoryContext();
        await SeedAsync(cached);
        await SeedAsync(replayed);
        var provider = new ChartProvider();

        var withCache = await PortfolioService(cached, provider, withSnapshots: true).GetPortfolioAsync("all", CancellationToken.None);
        var fullReplay = await PortfolioService(replayed, provider, withSnapshots: false).GetPortfolioAsync("all", CancellationToken.None);

        Assert.NotEmpty(cached.InvestmentPeriodSnapshots);
        Assert.Empty(replayed.InvestmentPeriodSnapshots);
        var expected = fullReplay.Chart.ToDictionary(point => point.Date);
        var compared = 0;
        foreach (var point in withCache.Chart)
        {
            Assert.True(expected.TryGetValue(point.Date, out var reference), $"The full replay has no point on {point.Date}.");
            Assert.Equal(Round(reference!.TotalValue), Round(point.TotalValue));
            Assert.Equal(Round(reference.NetDeposits), Round(point.NetDeposits));
            compared++;
        }
        // Monthly points from the cache, then the unsettled days valued live.
        Assert.True(compared > 20, $"Only {compared} points were compared.");
        Assert.Equal(Today, withCache.Chart[^1].Date);
        Assert.Contains(withCache.Chart, point => point.Date == InvestmentPeriodCalculator.MonthEnd(Start));
    }

    [Fact]
    public async Task AShortRangeNeverBuildsTheCache()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context);

        var portfolio = await PortfolioService(context, new ChartProvider(), withSnapshots: true)
            .GetPortfolioAsync("3m", CancellationToken.None);

        Assert.NotEmpty(portfolio.Chart);
        Assert.Empty(context.InvestmentPeriodSnapshots);
    }

    private static decimal? Round(decimal? value) => value is null ? null : Math.Round(value.Value, 6);

    private static InvestmentPortfolioService PortfolioService(AppDbContext context, IMarketDataProvider provider, bool withSnapshots)
    {
        var accounting = new InvestmentAccountingService();
        var snapshots = withSnapshots
            ? new InvestmentSnapshotService(
                context, accounting, provider,
                new FinancialClock(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Financial:TimeZoneId"] = "UTC" }).Build()),
                NullLogger<InvestmentSnapshotService>.Instance)
            : null;
        return new InvestmentPortfolioService(context, accounting, provider, periodSnapshots: snapshots);
    }

    // Twenty months of buys, a sale, a dividend, a fee, deposits and a withdrawal in USD, valued
    // in SGD. Bars land weekly and on every month-end, so the full replay is never thinned and
    // carries a point on every date the cached chart draws.
    private static async Task SeedAsync(AppDbContext context)
    {
        context.FinancialSettings.Add(new FinancialSetting { Currency = "SGD" });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var voo = new InvestmentInstrument { Symbol = "VOO", Name = "S&P 500", Type = "ETF", Currency = "USD", ProviderSymbol = "VOO", AllocationSleeve = "USEquity" };
        var bnd = new InvestmentInstrument { Symbol = "BND", Name = "Bonds", Type = "ETF", Currency = "USD", ProviderSymbol = "BND", AllocationSleeve = "Bonds" };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.AddRange(voo, bnd);
        await context.SaveChangesAsync();

        InvestmentTransaction Trade(InvestmentInstrument instrument, string type, int days, decimal units, decimal cash, decimal fees = 0) => new()
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument, Type = type,
            TradeDate = Start.AddDays(days), Units = units, UnitPrice = units == 0 ? null : cash / units, CashAmount = cash, Fees = fees,
        };
        InvestmentCashFlow Flow(string type, int days, decimal amount) => new()
        {
            AccountId = account.Id, Currency = "USD", Type = type, Amount = amount, Date = Start.AddDays(days),
        };
        context.InvestmentCashFlows.AddRange(
            Flow("Deposit", 2, 2000), Flow("Deposit", 130, 800), Flow("Withdrawal", 300, -150), Flow("Deposit", Today.DayNumber - Start.DayNumber - 3, 500));
        context.InvestmentTransactions.AddRange(
            Trade(voo, "Buy", 5, 10, 1000),
            Trade(bnd, "Buy", 40, 5, 500),
            Trade(voo, "Sell", 200, 3, 390, fees: 1),
            Trade(bnd, "Dividend", 250, 0, 12),
            Trade(voo, "FeeTax", 260, 0, 2),
            Trade(voo, "Buy", Today.DayNumber - Start.DayNumber - 2, 2, 300));

        var dates = new SortedSet<DateOnly>();
        for (var date = Start; date <= Today; date = date.AddDays(7)) dates.Add(date);
        for (var end = InvestmentPeriodCalculator.MonthEnd(Start); end <= Today; end = InvestmentPeriodCalculator.MonthEnd(end.AddDays(1))) dates.Add(end);
        foreach (var date in dates)
        {
            var days = date.DayNumber - Start.DayNumber;
            context.MarketPriceBars.Add(Bar("VOO|", date, 100m + days * 0.1m));
            context.MarketPriceBars.Add(Bar("BND|", date, 100m - days * 0.01m));
        }
        for (var date = Start.AddDays(-7); date <= Today; date = date.AddDays(1))
            context.FxRateBars.Add(new FxRateBar { Provider = "chart", BaseCurrency = "USD", QuoteCurrency = "SGD", MarketDate = date, Rate = Rate });
        await context.SaveChangesAsync();
    }

    private static MarketPriceBar Bar(string externalId, DateOnly date, decimal close) => new()
    {
        Provider = "chart", ExternalInstrumentId = externalId, Symbol = externalId.TrimEnd('|'), Mic = "", MarketDate = date, Close = close,
    };

    private sealed class ChartProvider : IMarketDataProvider
    {
        public MarketDataProviderDescriptor Descriptor => new(
            "chart", "Chart test data", true, MarketDataCapabilities.RequiredForActivation, new MarketDataQuotaPolicy(6, 750, 200));
        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(MarketInstrumentReference instrument, DateOnly startDate, CancellationToken cancellationToken)
            => throw new NotSupportedException("The chart never calls the provider.");
        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => throw new NotSupportedException("The chart never calls the provider.");
        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic)
            => string.IsNullOrWhiteSpace(symbol) ? null : new("chart", $"{symbol}|{mic}");
    }
}
