using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Investments;

/// <summary>
/// Reads the per-currency month-end rows in the app currency. Every month is converted at
/// today's rate, the same one for all of them, so the history shows what the markets did and a
/// rate change rescales it rather than rewriting it.
/// </summary>
internal static class InvestmentSnapshotConversion
{
    /// <summary>
    /// One row per month and segment, in <paramref name="appCurrency"/>. A currency with a figure
    /// to convert and no rate leaves that figure unknown and the month incomplete; zero needs no rate.
    /// </summary>
    public static IReadOnlyList<InvestmentPeriodSnapshot> Combine(
        IEnumerable<InvestmentPeriodSnapshot> rows,
        Func<string, decimal?> todayRate,
        string appCurrency)
        => rows
            .GroupBy(row => (row.PeriodEnd, row.Segment))
            .OrderBy(group => group.Key.PeriodEnd)
            .ThenBy(group => group.Key.Segment, StringComparer.Ordinal)
            .Select(group => CombineMonth(group.ToList(), todayRate, appCurrency))
            .ToList();

    private static InvestmentPeriodSnapshot CombineMonth(
        IReadOnlyList<InvestmentPeriodSnapshot> rows,
        Func<string, decimal?> todayRate,
        string appCurrency)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Where(value => value.MissingReason is not null))
            missing.Add(row.MissingReason!);

        decimal? Sum(Func<InvestmentPeriodSnapshot, decimal?> pick)
        {
            decimal? sum = 0;
            foreach (var row in rows)
            {
                var amount = pick(row);
                if (amount is null) { sum = null; continue; }
                if (amount == 0) continue;
                if (todayRate(row.Currency) is not { } rate)
                {
                    missing.Add($"No current {row.Currency}/{appCurrency.ToUpperInvariant()} rate.");
                    sum = null;
                    continue;
                }
                sum += amount * rate;
            }
            return sum;
        }

        var first = rows[0];
        var combined = new InvestmentPeriodSnapshot
        {
            UserId = first.UserId,
            PeriodEnd = first.PeriodEnd,
            Segment = first.Segment,
            Currency = appCurrency.ToUpperInvariant(),
            Provider = first.Provider,
            SchemaVersion = first.SchemaVersion,
            OpeningValue = Sum(value => value.OpeningValue),
            ClosingValue = Sum(value => value.ClosingValue),
            MarketValue = Sum(value => value.MarketValue),
            CashValue = Sum(value => value.CashValue),
            NetContributions = Sum(value => value.NetContributions),
            WeightedContributions = Sum(value => value.WeightedContributions),
            Dividends = Sum(value => value.Dividends),
            RealisedProfitLoss = Sum(value => value.RealisedProfitLoss),
            CostBasis = Sum(value => value.CostBasis),
            CarryState = rows.Select(value => value.CarryState).FirstOrDefault(value => value is not null),
            ComputedAt = rows.Max(value => value.ComputedAt),
        };
        combined.IsComplete = rows.All(value => value.IsComplete) && missing.Count == 0;
        combined.MissingReason = missing.Count == 0 ? null : string.Join(" ", missing);
        return combined;
    }
}
