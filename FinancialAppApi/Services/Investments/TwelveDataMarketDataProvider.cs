using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Services.Investments;

public sealed class TwelveDataMarketDataOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://api.twelvedata.com";
    public string? ApiKey { get; set; }
    public int RefreshCallsPerMinute { get; set; } = 6;
    public int DailyCallCeiling { get; set; } = 750;
    public int PerUserDailyCallCeiling { get; set; } = 200;
}

public sealed class TwelveDataMarketDataProvider(
    HttpClient client,
    IOptions<TwelveDataMarketDataOptions> options) : IMarketDataProvider
{
    public const string ProviderId = "twelvedata";
    private const char ReferenceSeparator = '|';
    private readonly TwelveDataMarketDataOptions _options = options.Value;

    public MarketDataProviderDescriptor Descriptor => new(
        ProviderId,
        "Twelve Data",
        _options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiKey),
        MarketDataCapabilities.RequiredForActivation,
        new MarketDataQuotaPolicy(
            _options.RefreshCallsPerMinute,
            _options.DailyCallCeiling,
            _options.PerUserDailyCallCeiling));

    public async Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var json = await GetJsonAsync(
            $"/symbol_search?symbol={Uri.EscapeDataString(query)}&outputsize=20",
            cancellationToken);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            ThrowProviderError(json.RootElement);
        }

        var results = new List<InstrumentSearchResult>();
        foreach (var item in data.EnumerateArray())
        {
            var type = NormalizeType(ReadString(item, "instrument_type"));
            if (type is null) continue;
            var symbol = ReadString(item, "symbol");
            var mic = ReadNullableString(item, "mic_code");
            var currency = ReadString(item, "currency").ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(symbol) || currency.Length != 3) continue;
            var availability = ReadAvailability(item);
            results.Add(new InstrumentSearchResult(
                symbol,
                ReadString(item, "instrument_name", symbol),
                type,
                ReadNullableString(item, "exchange"),
                mic,
                ReadNullableString(item, "country"),
                currency,
                availability,
                availability == MarketInstrumentAvailability.Unavailable
                    ? "This investment is unavailable on the configured market-data plan."
                    : null,
                CreateReference(symbol, mic)));
        }
        return results;
    }

    public async Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(
        MarketInstrumentReference instrument,
        DateOnly startDate,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        EnsureOwnReference(instrument);
        var (symbol, mic) = ParseReference(instrument.ExternalId);
        // Twelve Data rejects a start date that is still in the future for its own clock.
        var safeStart = Min(startDate, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
        var path = $"/time_series?symbol={Uri.EscapeDataString(symbol)}&interval=1day&start_date={safeStart:yyyy-MM-dd}&outputsize=5000";
        if (!string.IsNullOrWhiteSpace(mic))
        {
            path += $"&mic_code={Uri.EscapeDataString(mic)}";
        }
        using var json = await GetJsonAsync(path, cancellationToken);
        return ReadSeries(json.RootElement, "close")
            .Select(value => new ProviderPriceBar(value.Date, value.Value))
            .ToList();
    }

    public async Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(
        string baseCurrency,
        string quoteCurrency,
        DateOnly startDate,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var symbol = $"{baseCurrency.ToUpperInvariant()}/{quoteCurrency.ToUpperInvariant()}";
        using var json = await GetJsonAsync(
            $"/time_series?symbol={Uri.EscapeDataString(symbol)}&interval=1day&start_date={startDate:yyyy-MM-dd}&outputsize=5000",
            cancellationToken);
        return ReadSeries(json.RootElement, "close")
            .Select(value => new ProviderFxBar(value.Date, value.Value))
            .ToList();
    }

    public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic)
        => string.IsNullOrWhiteSpace(symbol) ? null : CreateReference(symbol, mic);

    public static MarketInstrumentReference CreateReference(string symbol, string? mic)
        => new(ProviderId, $"{symbol.Trim().ToUpperInvariant()}{ReferenceSeparator}{mic?.Trim().ToUpperInvariant() ?? string.Empty}");

    private static (string Symbol, string? Mic) ParseReference(string externalId)
    {
        var separator = externalId.IndexOf(ReferenceSeparator);
        if (separator <= 0)
            throw new MarketDataProviderException("The market-data reference is invalid.", MarketDataFailure.InvalidSymbol);
        var mic = externalId[(separator + 1)..];
        return (externalId[..separator], string.IsNullOrWhiteSpace(mic) ? null : mic);
    }

    private static DateOnly Min(DateOnly left, DateOnly right) => left <= right ? left : right;

    private void EnsureOwnReference(MarketInstrumentReference reference)
    {
        if (!reference.ProviderId.Equals(ProviderId, StringComparison.OrdinalIgnoreCase))
            throw new MarketDataProviderException("The market-data reference belongs to another provider.", MarketDataFailure.InvalidSymbol);
    }

    private async Task<JsonDocument> GetJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("apikey", _options.ApiKey);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new MarketDataProviderException("Market data is temporarily rate limited.", MarketDataFailure.RateLimited, response.Headers.RetryAfter?.Delta);
            if (!response.IsSuccessStatusCode)
                throw new MarketDataProviderException("Market data is temporarily unavailable.", MarketDataFailure.Provider);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MarketDataProviderException("The market data request timed out.", MarketDataFailure.Timeout, innerException: exception);
        }
        catch (JsonException exception)
        {
            throw new MarketDataProviderException("The market data response could not be read.", MarketDataFailure.MalformedResponse, innerException: exception);
        }
    }

    private static IReadOnlyList<(DateOnly Date, decimal Value)> ReadSeries(JsonElement root, string property)
    {
        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            ThrowProviderError(root);
        var result = new List<(DateOnly, decimal)>();
        foreach (var item in values.EnumerateArray())
        {
            if (DateOnly.TryParseExact(ReadString(item, "datetime"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                decimal.TryParse(ReadString(item, property), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
                result.Add((date, value));
        }
        return result;
    }

    private static void ThrowProviderError(JsonElement root)
    {
        var code = root.TryGetProperty("code", out var element) && element.TryGetInt32(out var parsed) ? parsed : 0;
        var message = ReadString(root, "message", "Market data is unavailable.");
        var failure = code switch
        {
            401 => MarketDataFailure.Configuration,
            404 => MarketDataFailure.InvalidSymbol,
            429 => MarketDataFailure.RateLimited,
            _ when message.Contains("plan", StringComparison.OrdinalIgnoreCase) => MarketDataFailure.UnavailablePlan,
            _ => MarketDataFailure.MalformedResponse
        };
        throw new MarketDataProviderException(
            failure == MarketDataFailure.InvalidSymbol ? "The symbol is unavailable." :
            failure == MarketDataFailure.UnavailablePlan ? "The investment is unavailable on the configured market-data plan." :
            "Market data is temporarily unavailable.", failure);
    }

    private void EnsureConfigured()
    {
        if (!Descriptor.IsConfigured)
            throw new MarketDataProviderException("Market data is not configured. Manual investments and prices remain available.", MarketDataFailure.Configuration);
    }

    private static string? NormalizeType(string type)
        => type.Equals("Common Stock", StringComparison.OrdinalIgnoreCase) ? "Stock" :
            type.Equals("ETF", StringComparison.OrdinalIgnoreCase) ? "ETF" :
            type.Equals("Mutual Fund", StringComparison.OrdinalIgnoreCase) || type.Equals("MutualFund", StringComparison.OrdinalIgnoreCase) ? "MutualFund" : null;

    private static MarketInstrumentAvailability ReadAvailability(JsonElement item)
    {
        if (!item.TryGetProperty("access", out var access)) return MarketInstrumentAvailability.Unknown;
        return access.ToString().Contains("premium", StringComparison.OrdinalIgnoreCase)
            ? MarketInstrumentAvailability.Unavailable
            : MarketInstrumentAvailability.Available;
    }

    private static string ReadString(JsonElement item, string name, string fallback = "")
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? fallback : fallback;

    private static string? ReadNullableString(JsonElement item, string name)
    {
        var result = ReadString(item, name);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
