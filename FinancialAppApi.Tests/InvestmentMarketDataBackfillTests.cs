using System.Net;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Tests;

/// <summary>
/// Market bars are a cache shared by every user. These pin that a refresh reaches back to the
/// oldest date the current user needs -- after a currency switch, a backdated entry, or a pair
/// another user already keeps fresh -- and that long histories arrive in bounded pages.
/// </summary>
public sealed class InvestmentMarketDataBackfillTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task FreshPairStoredForAnotherUserIsBackfilledToThisUsersOldestCashFlow()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var instrument = await SeedAsync(context, "SGD", Today.AddDays(-400));
        SeedPriceBars(context, instrument, Today.AddDays(-400), Today);
        // Another user's refresh keeps USD/SGD fresh, but only for the last month.
        for (var date = Today.AddDays(-30); date <= Today; date = date.AddDays(1))
            context.FxRateBars.Add(new FxRateBar { Provider = "test", BaseCurrency = "USD", QuoteCurrency = "SGD", MarketDate = date, Rate = 1.3m });
        await context.SaveChangesAsync();
        var provider = new RangeProvider();
        var service = NewService(context, provider);

        await service.RefreshAsync(CancellationToken.None);

        Assert.Equal([(Today.AddDays(-400), (DateOnly?)Today.AddDays(-31))], provider.FxRequests);
        Assert.Empty(provider.PriceRequests);
        Assert.Equal(Today.AddDays(-400), context.MarketSeriesCoverages
            .Single(value => value.Kind == MarketSeriesKinds.Fx && value.SeriesKey == "USD/SGD").CoveredFrom);
        Assert.Contains(context.FxRateBars, bar => bar.MarketDate < Today.AddDays(-300));

        var second = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal("Fresh", second.Status);
        Assert.Single(provider.FxRequests);
    }

    [Fact]
    public async Task BackdatedTradeOlderThanStoredPricesBackfillsTheInstrument()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var instrument = await SeedAsync(context, "USD", Today.AddDays(-10));
        SeedPriceBars(context, instrument, Today.AddDays(-10), Today);
        await context.SaveChangesAsync();
        var account = context.InvestmentAccounts.Single();
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument, Type = "Buy",
            TradeDate = Today.AddDays(-200), Units = 1, UnitPrice = 90, CashAmount = 90
        });
        await context.SaveChangesAsync();
        var provider = new RangeProvider();

        await NewService(context, provider).RefreshAsync(CancellationToken.None);

        Assert.Equal([(Today.AddDays(-200), (DateOnly?)Today.AddDays(-11))], provider.PriceRequests);
    }

    [Fact]
    public async Task HistoryLongerThanOneRequestArrivesInNewestFirstPagesUntilCovered()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var instrument = await SeedAsync(context, "USD", Today.AddDays(-250));
        var provider = new RangeProvider(maxDays: 100);
        var service = NewService(context, provider);

        MarketRefreshResponse result;
        var runs = 0;
        do
        {
            result = await service.RefreshAsync(CancellationToken.None);
            runs++;
        } while (!result.Complete && runs < 10);

        Assert.True(result.Complete);
        Assert.Equal(
            [
                (Today.AddDays(-99), (DateOnly?)null),
                (Today.AddDays(-199), Today.AddDays(-100)),
                (Today.AddDays(-250), Today.AddDays(-200)),
            ],
            provider.PriceRequests);
        Assert.Equal(Today.AddDays(-250), context.MarketSeriesCoverages
            .Single(value => value.Kind == MarketSeriesKinds.Price && value.SeriesKey == "VOO|").CoveredFrom);
        var job = context.MarketDataRefreshJobs.Single();
        Assert.Equal(job.TotalItems, job.UpdatedItems);
    }

    [Fact]
    public async Task OlderHistoryTheProviderDoesNotHaveIsAskedForOnlyOnce()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var instrument = await SeedAsync(context, "USD", Today.AddDays(-10));
        SeedPriceBars(context, instrument, Today.AddDays(-10), Today);
        await context.SaveChangesAsync();
        var account = context.InvestmentAccounts.Single();
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument, Type = "Buy",
            TradeDate = Today.AddDays(-200), Units = 1, UnitPrice = 90, CashAmount = 90
        });
        await context.SaveChangesAsync();
        var provider = new RangeProvider(noDataBefore: Today.AddDays(-12));
        var service = NewService(context, provider);

        await service.RefreshAsync(CancellationToken.None);
        var second = await service.RefreshAsync(CancellationToken.None);

        Assert.Single(provider.PriceRequests);
        Assert.Equal("Fresh", second.Status);
    }

    [Fact]
    public async Task TwelveDataAdapterSendsAnInclusiveEndDateAndDropsBarsPastIt()
    {
        HttpRequestMessage? captured = null;
        var handler = new DelegateHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"values":[{"datetime":"2030-01-11","close":"1.40"},{"datetime":"2030-01-10","close":"1.35"}]}
                    """)
            };
        });
        IMarketDataProvider provider = new TwelveDataMarketDataProvider(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test") },
            Options.Create(new TwelveDataMarketDataOptions { ApiKey = "key" }));

        var bars = await provider.GetFxSeriesAsync("USD", "SGD", new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 10), CancellationToken.None);

        Assert.Contains("start_date=2030-01-01", captured!.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("end_date=2030-01-11", captured.RequestUri.Query, StringComparison.Ordinal);
        Assert.Equal([new ProviderFxBar(new DateOnly(2030, 1, 10), 1.35m)], bars);
        Assert.True(provider.MaxSeriesDaysPerRequest is > 0 and < 5000);
    }

    private static InvestmentMarketDataService NewService(AppDbContext context, IMarketDataProvider provider) => new(
        context, provider, Options.Create(new MarketDataOptions()), NullLogger<InvestmentMarketDataService>.Instance);

    private static async Task<InvestmentInstrument> SeedAsync(AppDbContext context, string appCurrency, DateOnly firstDate)
    {
        context.FinancialSettings.Add(new FinancialSetting { Currency = appCurrency });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "VOO", Name = "Vanguard S&P 500 ETF", Type = "ETF", Currency = "USD", ProviderSymbol = "VOO"
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        context.InvestmentCashFlows.Add(new InvestmentCashFlow
        {
            AccountId = account.Id, Currency = "USD", Type = "Deposit", Amount = 1000, Date = firstDate
        });
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument, Type = "Buy",
            TradeDate = firstDate, Units = 10, UnitPrice = 100, CashAmount = 1000
        });
        await context.SaveChangesAsync();
        return instrument;
    }

    private static void SeedPriceBars(AppDbContext context, InvestmentInstrument instrument, DateOnly from, DateOnly to)
    {
        for (var date = from; date <= to; date = date.AddDays(1))
            context.MarketPriceBars.Add(new MarketPriceBar
            {
                Provider = "test", ExternalInstrumentId = $"{instrument.ProviderSymbol}|",
                Symbol = instrument.ProviderSymbol!, Mic = "", MarketDate = date, Close = 100m
            });
    }

    private sealed class RangeProvider(int maxDays = int.MaxValue, DateOnly? noDataBefore = null) : IMarketDataProvider
    {
        public List<(DateOnly Start, DateOnly? End)> PriceRequests { get; } = [];
        public List<(DateOnly Start, DateOnly? End)> FxRequests { get; } = [];
        public int MaxSeriesDaysPerRequest => maxDays;
        public MarketDataProviderDescriptor Descriptor => new(
            "test", "Test data", true, MarketDataCapabilities.RequiredForActivation,
            new MarketDataQuotaPolicy(6, 750, 200));

        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);

        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(MarketInstrumentReference instrument, DateOnly startDate, CancellationToken cancellationToken)
            => GetDailySeriesAsync(instrument, startDate, null, cancellationToken);

        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(MarketInstrumentReference instrument, DateOnly startDate, DateOnly? endDate, CancellationToken cancellationToken)
        {
            PriceRequests.Add((startDate, endDate));
            return Task.FromResult<IReadOnlyList<ProviderPriceBar>>(
                Dates(startDate, endDate).Select(date => new ProviderPriceBar(date, 100m)).ToList());
        }

        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => GetFxSeriesAsync(baseCurrency, quoteCurrency, startDate, null, cancellationToken);

        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(string baseCurrency, string quoteCurrency, DateOnly startDate, DateOnly? endDate, CancellationToken cancellationToken)
        {
            FxRequests.Add((startDate, endDate));
            return Task.FromResult<IReadOnlyList<ProviderFxBar>>(
                Dates(startDate, endDate).Select(date => new ProviderFxBar(date, 1.35m)).ToList());
        }

        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic)
            => string.IsNullOrWhiteSpace(symbol) ? null : new("test", $"{symbol}|{mic}");

        private IEnumerable<DateOnly> Dates(DateOnly start, DateOnly? end)
        {
            var first = noDataBefore is { } floor && floor > start ? floor : start;
            var last = end is { } value && value < Today ? value : Today;
            for (var date = first; date <= last; date = date.AddDays(1)) yield return date;
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
