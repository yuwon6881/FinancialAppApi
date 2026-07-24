using System.Globalization;

namespace FinancialAppApi.Services.Investments;

public sealed record CurrencyCatalogItem(string Code, string Symbol, string Name, string Label);

public static class CurrencyCatalog
{
    private static readonly IReadOnlyList<CurrencyCatalogItem> ItemsValue = Build();
    private static readonly HashSet<string> Codes = ItemsValue
        .Select(value => value.Code)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<CurrencyCatalogItem> Items => ItemsValue;

    public static bool Contains(string? code)
        => !string.IsNullOrWhiteSpace(code) && Codes.Contains(code.Trim());

    private static IReadOnlyList<CurrencyCatalogItem> Build()
    {
        var currencies = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(culture =>
            {
                try { return new RegionInfo(culture.Name); }
                catch (ArgumentException) { return null; }
            })
            .Where(region => region is not null)
            .Cast<RegionInfo>()
            .GroupBy(region => region.ISOCurrencySymbol, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var region = group.OrderBy(value => value.EnglishName).First();
                var code = region.ISOCurrencySymbol.ToUpperInvariant();
                var symbol = code switch
                {
                    "USD" => "US$",
                    "SGD" => "S$",
                    "CNY" => "CN¥",
                    "AUD" => "A$",
                    "CAD" => "C$",
                    "HKD" => "HK$",
                    _ => region.CurrencySymbol
                };
                return new CurrencyCatalogItem(
                    code,
                    symbol,
                    region.CurrencyEnglishName,
                    $"{code} ({symbol})");
            })
            .OrderBy(value => value.Code)
            .ToList();

        // ISO currencies that may not be represented by an installed .NET culture.
        var fallbacks = new[]
        {
            new CurrencyCatalogItem("XAF", "FCFA", "Central African CFA Franc", "XAF (FCFA)"),
            new CurrencyCatalogItem("XCD", "EC$", "East Caribbean Dollar", "XCD (EC$)"),
            new CurrencyCatalogItem("XOF", "CFA", "West African CFA Franc", "XOF (CFA)"),
            new CurrencyCatalogItem("XPF", "CFPF", "CFP Franc", "XPF (CFPF)")
        };
        currencies.AddRange(fallbacks.Where(item =>
            currencies.All(existing => !existing.Code.Equals(item.Code, StringComparison.OrdinalIgnoreCase))));
        return currencies.OrderBy(value => value.Code).ToList();
    }
}
