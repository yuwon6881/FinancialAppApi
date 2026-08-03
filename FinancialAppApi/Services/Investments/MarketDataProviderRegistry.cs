using Microsoft.Extensions.Options;

namespace FinancialAppApi.Services.Investments;

public sealed class MarketDataProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IMarketDataProvider> _providers;
    private readonly MarketDataOptions _options;

    public MarketDataProviderRegistry(
        IEnumerable<MarketDataProviderRegistration> registrations,
        IOptions<MarketDataOptions> options)
    {
        _options = options.Value;
        _providers = registrations.Select(value => value.Provider).ToDictionary(
            provider => provider.Descriptor.Id,
            StringComparer.OrdinalIgnoreCase);

        if (!_providers.TryGetValue(_options.ActiveProvider, out var active))
        {
            throw new InvalidOperationException(
                $"Unknown active market-data provider '{_options.ActiveProvider}'.");
        }

        var missing = MarketDataCapabilities.RequiredForActivation & ~active.Descriptor.Capabilities;
        if (missing != MarketDataCapabilities.None)
        {
            throw new InvalidOperationException(
                $"Market-data provider '{active.Descriptor.Id}' cannot be activated because it lacks {missing}.");
        }
    }

    public IMarketDataProvider ActiveProvider => GetRequired(_options.ActiveProvider);

    public IMarketDataProvider GetRequired(string providerId)
        => _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new InvalidOperationException($"Unknown market-data provider '{providerId}'.");

    public bool TryGet(string providerId, out IMarketDataProvider? provider)
        => _providers.TryGetValue(providerId, out provider);

    public IReadOnlyList<MarketDataProviderDescriptor> Providers
        => _providers.Values.Select(value => value.Descriptor).OrderBy(value => value.Id).ToList();
}

public sealed record MarketDataProviderRegistration(IMarketDataProvider Provider);
