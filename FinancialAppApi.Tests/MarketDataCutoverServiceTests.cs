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

    [Fact]
    public async Task ReadinessRefusesPortfolioDifferencesAboveTolerance()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "USD" });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "VOO", Name = "Fund", Type = "ETF", Currency = "USD"
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id,
            InstrumentId = instrument.Id,
            Instrument = instrument,
            Type = "Buy",
            TradeDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)),
            Units = 1,
            UnitPrice = 100,
            CashAmount = 100
        });
        context.InvestmentInstrumentMarketMappings.AddRange(
            new InvestmentInstrumentMarketMapping
            {
                InvestmentInstrumentId = instrument.Id,
                InvestmentInstrument = instrument,
                ProviderId = "active",
                ExternalInstrumentId = "active-voo",
                DisplaySymbol = "VOO"
            },
            new InvestmentInstrumentMarketMapping
            {
                InvestmentInstrumentId = instrument.Id,
                InvestmentInstrument = instrument,
                ProviderId = "candidate",
                ExternalInstrumentId = "candidate-voo",
                DisplaySymbol = "VOO"
            });
        context.MarketPriceBars.AddRange(
            Bar("active", "active-voo", 100),
            Bar("candidate", "candidate-voo", 110));
        await context.SaveChangesAsync();

        var options = Options.Create(new MarketDataOptions
        {
            ActiveProvider = "active",
            CutoverTolerancePercent = 2,
            FreshnessMinutes = 60
        });
        var registry = new MarketDataProviderRegistry(
            [
                new MarketDataProviderRegistration(new ComparisonProvider("active")),
                new MarketDataProviderRegistration(new ComparisonProvider("candidate"))
            ], options);
        var report = await new MarketDataCutoverService(
            context,
            registry,
            options,
            NullLogger<MarketDataCutoverService>.Instance)
            .ValidateAsync("candidate", approveDifferences: false, CancellationToken.None);

        Assert.Equal(1, report.PortfoliosCompared);
        Assert.Equal(1, report.PortfolioDifferencesAboveTolerance);
        Assert.False(report.Ready);
        Assert.False(report.DifferencesApproved);
    }

    private static MarketPriceBar Bar(string provider, string externalId, decimal close) => new()
    {
        Provider = provider,
        ExternalInstrumentId = externalId,
        Symbol = "VOO",
        MarketDate = DateOnly.FromDateTime(DateTime.UtcNow),
        Close = close,
        FetchedAt = DateTime.UtcNow
    };

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

    private sealed class ComparisonProvider(string id) : IMarketDataProvider
    {
        public MarketDataProviderDescriptor Descriptor => new(
            id, id, true, MarketDataCapabilities.RequiredForActivation,
            new MarketDataQuotaPolicy(10, 100, 100));

        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);

        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(
            MarketInstrumentReference instrument, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderPriceBar>>([]);

        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(
            string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderFxBar>>([]);

        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic) => null;
    }
}
