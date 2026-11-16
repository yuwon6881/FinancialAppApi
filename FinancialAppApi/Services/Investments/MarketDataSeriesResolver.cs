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

    /// <summary>
    /// The rate on or before <paramref name="date"/>: whichever of the direct pair, its inverse, or
    /// the cross through USD is freshest, so a long-unrefreshed direct pair never hides a current
    /// cross rate. A tie keeps that order of preference. A rate dated before
    /// <paramref name="notBefore"/> is not a current rate and is treated as missing, never shown.
    /// </summary>
    public ResolvedMarketFx? ResolveFx(
        string nativeCurrency,
        string appCurrency,
        DateOnly date,
        IReadOnlyList<FxRateBar> fxBars,
        DateOnly? notBefore = null)
    {
        if (nativeCurrency.Equals(appCurrency, StringComparison.OrdinalIgnoreCase))
            return new ResolvedMarketFx(1, date, "Same currency", null);

        var pair = ResolveProviderLeg(nativeCurrency, appCurrency, date, fxBars, notBefore);
        var nativeUsd = ResolveProviderLeg(nativeCurrency, "USD", date, fxBars, notBefore);
        var usdApp = ResolveProviderLeg("USD", appCurrency, date, fxBars, notBefore);
        var cross = nativeUsd is null || usdApp is null ||
                    nativeCurrency.Equals("USD", StringComparison.OrdinalIgnoreCase) ||
                    appCurrency.Equals("USD", StringComparison.OrdinalIgnoreCase)
            ? null
            : new ResolvedMarketFx(
                nativeUsd.Rate * usdApp.Rate,
                nativeUsd.Date < usdApp.Date ? nativeUsd.Date : usdApp.Date,
                $"{descriptor.DisplayName} USD cross",
                MinFetchedAt(nativeUsd.FetchedAt, usdApp.FetchedAt));
        return Freshest(pair, cross);
    }

    private ResolvedMarketFx? ResolveProviderLeg(
        string from,
        string to,
        DateOnly date,
        IReadOnlyList<FxRateBar> fxBars,
        DateOnly? notBefore)
    {
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase))
            return new ResolvedMarketFx(1, date, "Same currency", null);

        // A lowercase persisted currency must not cascade into an apparently empty portfolio.
        var direct = fxBars.Where(value =>
                value.BaseCurrency.Equals(from, StringComparison.OrdinalIgnoreCase) &&
                value.QuoteCurrency.Equals(to, StringComparison.OrdinalIgnoreCase) &&
                value.MarketDate <= date && (notBefore is null || value.MarketDate >= notBefore))
            .OrderByDescending(value => value.MarketDate)
            .FirstOrDefault();
        var inverse = fxBars.Where(value =>
                value.BaseCurrency.Equals(to, StringComparison.OrdinalIgnoreCase) &&
                value.QuoteCurrency.Equals(from, StringComparison.OrdinalIgnoreCase) &&
                value.MarketDate <= date && (notBefore is null || value.MarketDate >= notBefore) && value.Rate != 0)
            .OrderByDescending(value => value.MarketDate)
            .FirstOrDefault();
        return Freshest(
            direct is null ? null : new ResolvedMarketFx(direct.Rate, direct.MarketDate, $"{descriptor.DisplayName} direct", direct.FetchedAt),
            inverse is null ? null : new ResolvedMarketFx(1m / inverse.Rate, inverse.MarketDate, $"{descriptor.DisplayName} inverse", inverse.FetchedAt));
    }

    /// <summary>The later-dated of two rates; the first on a tie.</summary>
    private static ResolvedMarketFx? Freshest(ResolvedMarketFx? preferred, ResolvedMarketFx? other)
        => preferred is null ? other : other is null || other.Date <= preferred.Date ? preferred : other;

    private static DateTime? MinFetchedAt(DateTime? first, DateTime? second)
        => first is null ? second : second is null ? first : first < second ? first : second;
}
