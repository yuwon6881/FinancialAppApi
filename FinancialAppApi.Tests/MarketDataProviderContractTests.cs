using System.Net;
using FinancialAppApi.Services.Investments;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Tests;

public abstract class MarketDataProviderContractTests
{
    protected abstract IMarketDataProvider CreateProvider();

    [Fact]
    public async Task SearchReturnsProviderOwnedOpaqueReferences()
    {
        var provider = CreateProvider();

        var results = await provider.SearchAsync("VOO", CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(provider.Descriptor.Id, result.MarketDataReference.ProviderId);
        Assert.False(string.IsNullOrWhiteSpace(result.MarketDataReference.ExternalId));
        Assert.NotEqual(MarketInstrumentAvailability.Unavailable, result.Availability);
    }

    [Fact]
    public async Task PriceAndFxSeriesReturnPositiveNormalisedValues()
    {
        var provider = CreateProvider();
        var reference = (await provider.SearchAsync("VOO", CancellationToken.None)).Single().MarketDataReference;

        var prices = await provider.GetDailySeriesAsync(reference, new DateOnly(2026, 1, 1), CancellationToken.None);
        var rates = await provider.GetFxSeriesAsync("USD", "MYR", new DateOnly(2026, 1, 1), CancellationToken.None);

        Assert.All(prices, value => Assert.True(value.Close > 0));
        Assert.All(rates, value => Assert.True(value.Rate > 0));
    }
}

public sealed class TwelveDataProviderContractTests : MarketDataProviderContractTests
{
    protected override IMarketDataProvider CreateProvider()
    {
        var client = new HttpClient(new FixtureHandler()) { BaseAddress = new Uri("https://api.example.test") };
        return new TwelveDataMarketDataProvider(
            client,
            Options.Create(new TwelveDataMarketDataOptions { ApiKey = "test-key" }));
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.Contains("symbol_search", StringComparison.Ordinal)
                ? """{"data":[{"symbol":"VOO","instrument_name":"Vanguard S&P 500 ETF","instrument_type":"ETF","currency":"USD","mic_code":"ARCX","access":"basic"}]}"""
                : """{"values":[{"datetime":"2026-01-02","close":"4.25"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}

public sealed class MarketDataProviderRegistryTests
{
    [Fact]
    public void SelectsTheConfiguredProviderWithoutFallback()
    {
        var first = new StubProvider("first", configured: true);
        var second = new StubProvider("second", configured: true);

        var registry = Registry("second", first, second);

        Assert.Same(second, registry.ActiveProvider);
    }

    [Fact]
    public void RejectsAnUnknownActiveProvider()
        => Assert.Throws<InvalidOperationException>(() => Registry("missing", new StubProvider("known", true)));

    [Fact]
    public void KeepsAKnownButUnconfiguredProviderAsAGracefulManualMode()
        => Assert.False(Registry("known", new StubProvider("known", false)).ActiveProvider.Descriptor.IsConfigured);

    private static MarketDataProviderRegistry Registry(string active, params IMarketDataProvider[] providers)
        => new(
            providers.Select(value => new MarketDataProviderRegistration(value)),
            Options.Create(new MarketDataOptions { ActiveProvider = active }));

    private sealed class StubProvider(string id, bool configured) : IMarketDataProvider
    {
        public MarketDataProviderDescriptor Descriptor => new(
            id, id, configured, MarketDataCapabilities.RequiredForActivation,
            new MarketDataQuotaPolicy(1, 1, 1));
        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);
        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(MarketInstrumentReference instrument, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderPriceBar>>([]);
        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderFxBar>>([]);
        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic) => null;
    }
}
