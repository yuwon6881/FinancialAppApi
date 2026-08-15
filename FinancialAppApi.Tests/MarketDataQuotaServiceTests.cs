using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public sealed class MarketDataQuotaServiceTests
{
    [Fact]
    public async Task RefreshAllowanceIsSharedByProviderAcrossUsersButNotAcrossProviders()
    {
        await using var context = TestHelpers.NewInMemoryContext("alice");
        var first = new TestProvider("first", new MarketDataQuotaPolicy(1, 10, 10));
        var second = new TestProvider("second", new MarketDataQuotaPolicy(1, 10, 10));

        var alice = new MarketDataQuotaService(context, first);
        Assert.Equal(1, await alice.ReserveRefreshAsync(1, CancellationToken.None));

        context.SetCurrentUser("bob");
        var bob = new MarketDataQuotaService(context, first);
        Assert.Equal(0, await bob.ReserveRefreshAsync(1, CancellationToken.None));

        var otherProvider = new MarketDataQuotaService(context, second);
        Assert.Equal(1, await otherProvider.ReserveRefreshAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task ExceedingTheSharedAllowanceDegradesToZeroCalls()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var provider = new TestProvider("limited", new MarketDataQuotaPolicy(2, 2, 2));
        var service = new MarketDataQuotaService(context, provider);

        Assert.Equal(2, await service.ReserveRefreshAsync(10, CancellationToken.None));
        Assert.Equal(0, await service.ReserveRefreshAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task FullyLoadedPortfolioDoesNotSpendProviderCalls()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "USD" });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "LOADED", Name = "Loaded fund", Type = "ETF", Currency = "USD"
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        context.InvestmentInstrumentMarketMappings.Add(new InvestmentInstrumentMarketMapping
        {
            InvestmentInstrumentId = instrument.Id,
            InvestmentInstrument = instrument,
            ProviderId = "loaded",
            ExternalInstrumentId = "loaded-id",
            DisplaySymbol = instrument.Symbol
        });
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id,
            InstrumentId = instrument.Id,
            Instrument = instrument,
            Type = "Buy",
            TradeDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            Units = 1,
            UnitPrice = 100,
            CashAmount = 100
        });
        context.MarketPriceBars.Add(new MarketPriceBar
        {
            Provider = "loaded",
            ExternalInstrumentId = "loaded-id",
            Symbol = instrument.Symbol,
            MarketDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Close = 110,
            FetchedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var provider = new TestProvider("loaded", new MarketDataQuotaPolicy(1, 1, 1));
        var portfolio = await new InvestmentPortfolioService(
            context, new InvestmentAccountingService(), provider)
            .GetPortfolioAsync("all", CancellationToken.None);

        Assert.Equal(110, portfolio.Summary.MarketValue);
        Assert.Equal(0, provider.CallCount);
    }

    private sealed class TestProvider(string id, MarketDataQuotaPolicy policy) : IMarketDataProvider
    {
        public int CallCount { get; private set; }

        public MarketDataProviderDescriptor Descriptor => new(
            id, id, true, MarketDataCapabilities.RequiredForActivation, policy);

        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);
        }

        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(
            MarketInstrumentReference instrument, DateOnly startDate, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<ProviderPriceBar>>([]);
        }

        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(
            string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<ProviderFxBar>>([]);
        }

        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic)
        {
            CallCount++;
            return new MarketInstrumentReference(id, symbol ?? "unknown");
        }
    }
}
