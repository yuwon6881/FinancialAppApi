using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public sealed class InstrumentHistoryServiceTests
{
    [Fact]
    public async Task ReturnsPriceHistoryWithCombinedAveragePricePaidAcrossAccounts()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var broker = new InvestmentAccount { Name = "Broker A", BaseCurrency = "USD" };
        var other = new InvestmentAccount { Name = "Broker B", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "VOO", Name = "Vanguard S&P 500 ETF", Type = "ETF", Currency = "USD",
            ProviderSymbol = "VOO", ProviderMic = "ARCX"
        };
        context.InvestmentAccounts.AddRange(broker, other);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();

        // The same fund bought in two brokers reads as one fund, so the average
        // price paid must span both.
        context.InvestmentTransactions.AddRange(
            new InvestmentTransaction
            {
                AccountId = broker.Id, InstrumentId = instrument.Id, Instrument = instrument,
                Type = "Buy", TradeDate = new DateOnly(2026, 1, 5), Units = 2, UnitPrice = 100, CashAmount = 200
            },
            new InvestmentTransaction
            {
                AccountId = other.Id, InstrumentId = instrument.Id, Instrument = instrument,
                Type = "Buy", TradeDate = new DateOnly(2026, 2, 5), Units = 2, UnitPrice = 200, CashAmount = 400
            });
        context.MarketPriceBars.AddRange(
            PriceBar(new DateOnly(2026, 1, 5), 100),
            PriceBar(new DateOnly(2026, 2, 5), 200));
        await context.SaveChangesAsync();

        var history = await NewService(context).GetAsync(instrument.Id, "all", CancellationToken.None);

        Assert.NotNull(history);
        Assert.Equal(4m, history.Units);
        Assert.Equal(150m, history.AverageCostNative);
        Assert.Equal(200m, history.LatestPriceNative);
        Assert.Equal(new DateOnly(2026, 1, 5), history.FirstBoughtOn);
        Assert.Equal([100m, 200m], history.Points.Select(point => point.Price));
    }

    [Fact]
    public async Task PrefersTheMostRecentlyFetchedBarAndIgnoresMicCasing()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var instrument = new InvestmentInstrument
        {
            Symbol = "VOO", Name = "Vanguard S&P 500 ETF", Type = "ETF", Currency = "USD",
            ProviderSymbol = "VOO", ProviderMic = "ARCX"
        };
        context.InvestmentInstruments.Add(instrument);
        context.MarketPriceBars.AddRange(
            new MarketPriceBar
            {
                Provider = "test", ExternalInstrumentId = "VOO|ARCX", Symbol = "VOO", Mic = "arcx", MarketDate = new DateOnly(2026, 3, 2), Close = 10,
                FetchedAt = new DateTime(2026, 3, 2, 1, 0, 0, DateTimeKind.Utc)
            },
            new MarketPriceBar
            {
                Provider = "test", ExternalInstrumentId = "VOO|ARCX", Symbol = "VOO", Mic = "ARCX", MarketDate = new DateOnly(2026, 3, 2), Close = 12,
                FetchedAt = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc)
            });
        await context.SaveChangesAsync();

        var history = await NewService(context).GetAsync(instrument.Id, "all", CancellationToken.None);

        Assert.NotNull(history);
        Assert.Equal(12m, Assert.Single(history.Points).Price);
    }

    [Fact]
    public async Task ReportsNoHistoryForACustomInstrumentRatherThanFailing()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var instrument = new InvestmentInstrument
        {
            Symbol = "PRIVATE", Name = "Private holding", Type = "Other", Currency = "USD", IsCustom = true
        };
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();

        var history = await NewService(context).GetAsync(instrument.Id, "all", CancellationToken.None);

        Assert.NotNull(history);
        Assert.Empty(history.Points);
        Assert.Null(history.LatestPriceNative);
        Assert.Null(history.AverageCostNative);
    }

    [Fact]
    public async Task ReturnsNullForAnInstrumentThatDoesNotExist()
    {
        await using var context = TestHelpers.NewInMemoryContext();

        Assert.Null(await NewService(context).GetAsync(Guid.NewGuid(), "all", CancellationToken.None));
    }

    private static InstrumentHistoryService NewService(Database.AppDbContext context)
        => new(context, new InvestmentAccountingService(), new StubProvider());

    private static MarketPriceBar PriceBar(DateOnly date, decimal close) => new()
    {
        Provider = "test", ExternalInstrumentId = "VOO|ARCX", Symbol = "VOO", Mic = "ARCX",
        MarketDate = date, Close = close
    };

    private sealed class StubProvider : IMarketDataProvider
    {
        public MarketDataProviderDescriptor Descriptor => new(
            "test", "Test data", true, MarketDataCapabilities.RequiredForActivation,
            new MarketDataQuotaPolicy(6, 750, 200));
        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);
        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(MarketInstrumentReference instrument, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderPriceBar>>([]);
        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderFxBar>>([]);
        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic)
            => string.IsNullOrWhiteSpace(symbol) ? null : new("test", $"{symbol}|{mic}");
    }
}

public sealed class InvestmentChartRangeTests
{
    [Fact]
    public void CapsAnySeriesAtTheChartPointLimitAndAlwaysKeepsTheNewestPoint()
    {
        var values = Enumerable.Range(0, 5000).ToList();

        var sampled = InvestmentChartRange.Sample(values);

        Assert.True(sampled.Count <= InvestmentChartRange.MaxPoints + 1);
        Assert.Equal(4999, sampled[^1]);
        Assert.Equal(0, sampled[0]);
    }

    [Fact]
    public void LeavesShortSeriesUntouched()
    {
        var values = Enumerable.Range(0, 12).ToList();

        Assert.Equal(values, InvestmentChartRange.Sample(values));
    }

    [Theory]
    [InlineData("3y", -3)]
    [InlineData("5y", -5)]
    public void SupportsTheLongRanges(string range, int years)
    {
        var today = new DateOnly(2026, 8, 3);

        Assert.Equal(today.AddYears(years), InvestmentChartRange.StartFor(range, today));
    }

    [Fact]
    public void TreatsAllAsOpenEndedSoCallersSupplyTheirOwnEarliestDate()
    {
        Assert.Null(InvestmentChartRange.StartFor("all", new DateOnly(2026, 8, 3)));
    }
}
