using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Tests;

public sealed class MarketDataCutoverServiceTests
{
    [Fact]
    public async Task ShadowBackfillIsIdempotentForMappingsAndStoredBars()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "VOO", Name = "Fund", Type = "ETF", Currency = "USD", ProviderSymbol = "VOO"
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
            Type = "Buy", TradeDate = new DateOnly(2026, 1, 2), Units = 1, UnitPrice = 10, CashAmount = 10
        });
        await context.SaveChangesAsync();
        var provider = new StubProvider();
        var options = Options.Create(new MarketDataOptions { ActiveProvider = "shadow" });
        var registry = new MarketDataProviderRegistry(
            [new MarketDataProviderRegistration(provider)], options);
        var service = new MarketDataCutoverService(
            context, registry, options, NullLogger<MarketDataCutoverService>.Instance);

        var first = await service.BackfillAsync("shadow", CancellationToken.None);
        var second = await service.BackfillAsync("shadow", CancellationToken.None);

        Assert.Equal(1, first.MappingsCreated);
        Assert.Equal(0, second.MappingsCreated);
        Assert.Single(context.InvestmentInstrumentMarketMappings);
        Assert.Single(context.MarketPriceBars);
    }

    private sealed class StubProvider : IMarketDataProvider
    {
        public MarketDataProviderDescriptor Descriptor => new(
            "shadow", "Shadow data", true, MarketDataCapabilities.RequiredForActivation,
            new MarketDataQuotaPolicy(10, 100, 100));

        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([
                new InstrumentSearchResult(
                    query, "Fund", "ETF", "NYSE Arca", "ARCX", "United States", "USD",
                    MarketInstrumentAvailability.Available, null,
                    new MarketInstrumentReference("shadow", $"instrument:{query}"))
            ]);

        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(
            MarketInstrumentReference instrument, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderPriceBar>>([new ProviderPriceBar(startDate, 12m)]);

        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(
            string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderFxBar>>([new ProviderFxBar(startDate, 1m)]);

        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic) => null;
    }
}
