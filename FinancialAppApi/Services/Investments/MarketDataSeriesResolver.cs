using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Investments;

internal sealed record ResolvedMarketPrice(DateOnly Date, decimal Price, DateTime FetchedAt);

internal sealed record ResolvedMarketFx(decimal Rate, DateOnly Date, string Source, DateTime? FetchedAt);

internal sealed class MarketDataSeriesResolver(MarketDataProviderDescriptor descriptor)
{
    public List<ResolvedMarketPrice> ResolvePrices(
        MarketInstrumentReference? reference,
        IReadOnlyList<MarketPriceBar> priceBars)
    {
        if (reference is null) return [];
        return priceBars
            .Where(value => value.ExternalInstrumentId.Equals(reference.ExternalId, StringComparison.Ordinal))
            .Select(value => new ResolvedMarketPrice(value.MarketDate, value.Close, value.FetchedAt))
            .GroupBy(value => value.Date)
            .Select(group => group.OrderByDescending(value => value.FetchedAt).First())
            .OrderBy(value => value.Date)
            .ToList();
    }

    public ResolvedMarketFx? ResolveFx(
        string nativeCurrency,
        string appCurrency,
        DateOnly date,
        IReadOnlyList<FxRateBar> fxBars)
    {
        if (nativeCurrency.Equals(appCurrency, StringComparison.OrdinalIgnoreCase))
            return new ResolvedMarketFx(1, date, "Same currency", null);

        // A lowercase persisted currency must not cascade into an apparently empty portfolio.
        var direct = fxBars
            .Where(value =>
                value.BaseCurrency.Equals(nativeCurrency, StringComparison.OrdinalIgnoreCase) &&
                value.QuoteCurrency.Equals(appCurrency, StringComparison.OrdinalIgnoreCase) &&
                value.MarketDate <= date)
            .OrderByDescending(value => value.MarketDate)
            .FirstOrDefault();
        if (direct is not null)
            return new ResolvedMarketFx(direct.Rate, direct.MarketDate, $"{descriptor.DisplayName} direct", direct.FetchedAt);

        var inverse = fxBars.Where(value =>
                value.BaseCurrency.Equals(appCurrency, StringComparison.OrdinalIgnoreCase) &&
                value.QuoteCurrency.Equals(nativeCurrency, StringComparison.OrdinalIgnoreCase) &&
                value.MarketDate <= date && value.Rate != 0)
            .OrderByDescending(value => value.MarketDate)
            .FirstOrDefault();
        if (inverse is not null)
            return new ResolvedMarketFx(1m / inverse.Rate, inverse.MarketDate, $"{descriptor.DisplayName} inverse", inverse.FetchedAt);

        var nativeUsd = ResolveProviderLeg(nativeCurrency, "USD", date, fxBars);
        var usdApp = ResolveProviderLeg("USD", appCurrency, date, fxBars);
        if (nativeUsd is null || usdApp is null) return null;
        return new ResolvedMarketFx(
            nativeUsd.Rate * usdApp.Rate,
            nativeUsd.Date < usdApp.Date ? nativeUsd.Date : usdApp.Date,
            $"{descriptor.DisplayName} USD cross",
            MinFetchedAt(nativeUsd.FetchedAt, usdApp.FetchedAt));
    }

    private ResolvedMarketFx? ResolveProviderLeg(
        string from,
        string to,
        DateOnly date,
        IReadOnlyList<FxRateBar> fxBars)
    {
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase))
            return new ResolvedMarketFx(1, date, "Same currency", null);

        var direct = fxBars.Where(value =>
                value.BaseCurrency.Equals(from, StringComparison.OrdinalIgnoreCase) &&
                value.QuoteCurrency.Equals(to, StringComparison.OrdinalIgnoreCase) &&
                value.MarketDate <= date)
            .OrderByDescending(value => value.MarketDate)
            .FirstOrDefault();
        if (direct is not null)
            return new ResolvedMarketFx(direct.Rate, direct.MarketDate, $"{descriptor.DisplayName} direct", direct.FetchedAt);

        var inverse = fxBars.Where(value =>
                value.BaseCurrency.Equals(to, StringComparison.OrdinalIgnoreCase) &&
                value.QuoteCurrency.Equals(from, StringComparison.OrdinalIgnoreCase) &&
                value.MarketDate <= date && value.Rate != 0)
            .OrderByDescending(value => value.MarketDate)
            .FirstOrDefault();
        return inverse is null
            ? null
            : new ResolvedMarketFx(1m / inverse.Rate, inverse.MarketDate, $"{descriptor.DisplayName} inverse", inverse.FetchedAt);
    }

    private static DateTime? MinFetchedAt(DateTime? first, DateTime? second)
        => first is null ? second : second is null ? first : first < second ? first : second;
}
