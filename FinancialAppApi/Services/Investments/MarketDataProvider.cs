using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FinancialAppApi.Services.Investments;

public sealed class MarketDataOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://api.twelvedata.com";
    public string? TwelveDataApiKey { get; set; }
    public int FreshnessMinutes { get; set; } = 15;
    public int RefreshCallsPerMinute { get; set; } = 6;
    public int DailyCallCeiling { get; set; } = 750;
}

public sealed record InstrumentSearchResult(
    string Symbol,
    string Name,
    string Type,
    string? Exchange,
    string? Mic,
    string? Country,
    string Currency,
    bool AvailableOnBasic,
    string Provider);

public sealed record ProviderPriceBar(DateOnly Date, decimal Close);
public sealed record ProviderFxBar(DateOnly Date, decimal Rate);

public interface IMarketDataProvider
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(
        string symbol, string? mic, DateOnly startDate, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(
        string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken);
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

public sealed class TwelveDataMarketDataProvider(
    HttpClient client,
    Microsoft.Extensions.Options.IOptions<MarketDataOptions> options) : IMarketDataProvider
{
    private readonly MarketDataOptions _options = options.Value;

    public bool IsConfigured =>
        _options.Enabled &&
        !string.IsNullOrWhiteSpace(_options.TwelveDataApiKey);

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
            var type = ReadString(item, "instrument_type");
            if (type.Equals("Common Stock", StringComparison.OrdinalIgnoreCase)) type = "Stock";
            if (type.Equals("Mutual Fund", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("MutualFund", StringComparison.OrdinalIgnoreCase))
                type = "MutualFund";
            if (!type.Equals("Stock", StringComparison.OrdinalIgnoreCase) &&
                !type.Equals("ETF", StringComparison.OrdinalIgnoreCase) &&
                !type.Equals("MutualFund", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var symbol = ReadString(item, "symbol");
            var currency = ReadString(item, "currency").ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(symbol) || currency.Length != 3) continue;
            results.Add(new InstrumentSearchResult(
                symbol,
                ReadString(item, "instrument_name", symbol),
                type.Equals("ETF", StringComparison.OrdinalIgnoreCase)
                    ? "ETF"
                    : type.Equals("MutualFund", StringComparison.OrdinalIgnoreCase) ? "MutualFund" : "Stock",
                ReadNullableString(item, "exchange"),
                ReadNullableString(item, "mic_code"),
                ReadNullableString(item, "country"),
                currency,
                ReadBasicAvailability(item),
                "twelvedata"));
        }
        return results;
    }

    public async Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(
        string symbol,
        string? mic,
        DateOnly startDate,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var path = $"/time_series?symbol={Uri.EscapeDataString(symbol)}&interval=1day&start_date={startDate:yyyy-MM-dd}&outputsize=5000";
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

    private async Task<JsonDocument> GetJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("apikey", _options.TwelveDataApiKey);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta;
                throw new MarketDataProviderException(
                    "Market data is temporarily rate limited.",
                    MarketDataFailure.RateLimited,
                    retryAfter);
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new MarketDataProviderException(
                    "Market data is temporarily unavailable.",
                    MarketDataFailure.Provider);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MarketDataProviderException(
                "The market data request timed out.",
                MarketDataFailure.Timeout,
                innerException: exception);
        }
        catch (JsonException exception)
        {
            throw new MarketDataProviderException(
                "The market data response could not be read.",
                MarketDataFailure.MalformedResponse,
                innerException: exception);
        }
    }

    private static IReadOnlyList<(DateOnly Date, decimal Value)> ReadSeries(
        JsonElement root,
        string valueProperty)
    {
        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            ThrowProviderError(root);
        }
        var result = new List<(DateOnly, decimal)>();
        foreach (var item in values.EnumerateArray())
        {
            if (!DateOnly.TryParseExact(
                    ReadString(item, "datetime"),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date) ||
                !decimal.TryParse(
                    ReadString(item, valueProperty),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value) ||
                value <= 0)
            {
                continue;
            }
            result.Add((date, value));
        }
        return result;
    }

    private static void ThrowProviderError(JsonElement root)
    {
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed)
            ? parsed
            : 0;
        var message = ReadString(root, "message", "Market data is unavailable.");
        var failure = code switch
        {
            401 => MarketDataFailure.Configuration,
            404 => MarketDataFailure.InvalidSymbol,
            429 => MarketDataFailure.RateLimited,
            _ when message.Contains("plan", StringComparison.OrdinalIgnoreCase) => MarketDataFailure.UnavailablePlan,
            _ => MarketDataFailure.MalformedResponse
        };
        // Provider text may contain request/account details. Keep it out of the public exception.
        throw new MarketDataProviderException(
            failure == MarketDataFailure.InvalidSymbol ? "The symbol is unavailable." :
            failure == MarketDataFailure.UnavailablePlan ? "The symbol is unavailable on the configured plan." :
            "Market data is temporarily unavailable.",
            failure);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new MarketDataProviderException(
                "Market data is not configured. Manual investments and prices remain available.",
                MarketDataFailure.Configuration);
        }
    }

    private static string ReadString(JsonElement item, string name, string fallback = "")
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? fallback
            : fallback;

    private static string? ReadNullableString(JsonElement item, string name)
    {
        var result = ReadString(item, name);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static bool ReadBasicAvailability(JsonElement item)
    {
        if (!item.TryGetProperty("access", out var access)) return true;
        return !access.ToString().Contains("premium", StringComparison.OrdinalIgnoreCase);
    }
}
