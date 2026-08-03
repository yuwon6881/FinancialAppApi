namespace FinancialAppApi.Services.Investments;

public sealed class MarketDataOptions
{
    public bool Enabled { get; set; } = true;
    public string ActiveProvider { get; set; } = string.Empty;
    public int FreshnessMinutes { get; set; } = 15;
    public decimal CutoverTolerancePercent { get; set; } = 2m;
    public int RollbackRetentionDays { get; set; } = 14;
}

[Flags]
public enum MarketDataCapabilities
{
    None = 0,
    InstrumentSearch = 1,
    DailyPrices = 2,
    ExchangeRates = 4,
    RequiredForActivation = InstrumentSearch | DailyPrices | ExchangeRates
}

public enum MarketInstrumentAvailability
{
    Unknown,
    Available,
    Unavailable
}

public sealed record MarketDataProviderDescriptor(
    string Id,
    string DisplayName,
    bool IsConfigured,
    MarketDataCapabilities Capabilities,
    MarketDataQuotaPolicy QuotaPolicy);

public sealed record MarketDataQuotaPolicy(
    int RefreshCallsPerMinute,
    int DailyCallCeiling,
    int PerUserDailyCallCeiling,
    int DiscoveryCallsPerMinute = 2);

public sealed record MarketInstrumentReference(string ProviderId, string ExternalId);

public sealed record InstrumentSearchResult(
    string Symbol,
    string Name,
    string Type,
    string? Exchange,
    string? Mic,
    string? Country,
    string Currency,
    MarketInstrumentAvailability Availability,
    string? AvailabilityMessage,
    MarketInstrumentReference MarketDataReference);

public sealed record ProviderPriceBar(DateOnly Date, decimal Close);
public sealed record ProviderFxBar(DateOnly Date, decimal Rate);

public interface IMarketDataProvider
{
    MarketDataProviderDescriptor Descriptor { get; }

    Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(
        MarketInstrumentReference instrument,
        DateOnly startDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(
        string baseCurrency,
        string quoteCurrency,
        DateOnly startDate,
        CancellationToken cancellationToken);

    MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic);
}

public sealed class MarketDataProviderException(
    string message,
    MarketDataFailure failure,
    TimeSpan? retryAfter = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public MarketDataFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public enum MarketDataFailure
{
    RateLimited,
    Timeout,
    InvalidSymbol,
    UnavailablePlan,
    MalformedResponse,
    Configuration,
    Provider
}
