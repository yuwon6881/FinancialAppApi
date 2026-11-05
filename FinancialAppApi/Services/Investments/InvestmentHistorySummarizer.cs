using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Investments;

public sealed record InvestmentHistoryMonthDto(
    DateOnly PeriodEnd,
    decimal? ClosingValue,
    decimal? NetContributions,
    // Everything put in since the first month, so a chart can draw contributions against value.
    decimal? CumulativeContributions,
    decimal? ReturnPercent,
    bool IsComplete);

public sealed record InvestmentHistorySegmentDto(
    string Segment,
    decimal? OpeningValue,
    decimal? ClosingValue,
    decimal? NetContributions,
    decimal? Growth,
    decimal? ReturnPercent,
    bool IsComplete);

public sealed record InvestmentHistoryYearDto(
    int Year,
    DateOnly PeriodEnd,
    // False while the year still has months to settle: its figures run only to PeriodEnd.
    bool IsFullYear,
    decimal? OpeningValue,
    decimal? ClosingValue,
    decimal? NetContributions,
    decimal? Dividends,
    decimal? RealisedProfitLoss,
    decimal? Growth,
    decimal? ReturnPercent,
    bool IsComplete,
    IReadOnlyList<string> MissingReasons,
    IReadOnlyList<InvestmentHistorySegmentDto> Segments);

public sealed record InvestmentHistoryLifetimeDto(
    DateOnly? FirstMonth,
    DateOnly? AsOf,
    decimal? NetContributions,
    decimal? Value,
    decimal? Growth,
    decimal? Dividends,
    decimal? RealisedProfitLoss,
    // Growth against what was at stake, like every other period's return.
    decimal? CumulativeReturnPercent,
    // Money-weighted yearly rate (the rate that grows each contribution, from when it arrived, into
    // today's value). Null until a full year of history exists, so a short run is never extrapolated.
    decimal? AnnualisedReturnPercent,
    bool IsComplete,
    IReadOnlyList<InvestmentHistorySegmentDto> Segments);

public sealed record InvestmentHistoryDto(
    string Currency,
    InvestmentHistoryLifetimeDto Lifetime,
    IReadOnlyList<InvestmentHistoryYearDto> Years,
    IReadOnlyList<InvestmentHistoryMonthDto> Months);

/// <summary>
/// Turns stored month-end snapshots into the long-range history: months, calendar years, and the
/// lifetime, overall and per market. Pure, and its cost grows only with the number of months.
///
/// A month's return is its Modified Dietz return. A longer period's return is its growth against
/// what was at stake (the opening value plus every month's money added), so it always carries the
/// same sign as the growth shown beside it: a chained, time-weighted return could read +0.8% beside
/// a loss when a small early gain was followed by a loss on a larger sum. The lifetime's yearly rate
/// is money-weighted for the same reason. A month whose figures cannot be known (a missing price or
/// rate) leaves every period containing it unknown rather than silently skipping it. Percentages are
/// percent (12.5 means 12.5%).
/// </summary>
public static class InvestmentHistorySummarizer
{
    private const int MaxReasons = 5;

    public static InvestmentHistoryDto Summarize(string currency, IReadOnlyList<InvestmentPeriodSnapshot> snapshots)
    {
        var byMonth = snapshots
            .GroupBy(value => value.PeriodEnd)
            .OrderBy(group => group.Key)
            .Select(group => new MonthRows(
                group.Key,
                group.FirstOrDefault(value => value.Segment == InvestmentSnapshotSegments.Total),
                group.Where(value => value.Segment != InvestmentSnapshotSegments.Total)
                    .ToDictionary(value => value.Segment, StringComparer.Ordinal)))
            .Where(month => month.Total is not null)
            .ToList();

        var months = new List<InvestmentHistoryMonthDto>();
        decimal? cumulative = 0;
        foreach (var month in byMonth)
        {
            cumulative += month.Total!.NetContributions;
            months.Add(new InvestmentHistoryMonthDto(
                month.PeriodEnd,
                month.Total.ClosingValue,
                month.Total.NetContributions,
                cumulative,
                ToPercent(MonthFactor(month.Total) - 1),
                month.Total.IsComplete));
        }

        var segmentNames = byMonth.SelectMany(month => month.Segments.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var years = byMonth
            .GroupBy(month => month.PeriodEnd.Year)
            .Select(group => Year(group.Key, group.ToList(), segmentNames))
            .OrderByDescending(year => year.Year)
            .ToList();

        return new InvestmentHistoryDto(currency.ToUpperInvariant(), Lifetime(byMonth, segmentNames), years, months);
    }

    private static InvestmentHistoryYearDto Year(int year, IReadOnlyList<MonthRows> months, IReadOnlyList<string> segmentNames)
    {
        var totals = months.Select(month => month.Total!).ToList();
        var period = Period(totals);
        var reasons = totals.Where(value => value.MissingReason is not null)
            .SelectMany(value => value.MissingReason!.Split(". ", StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.EndsWith('.') ? value : value + ".")
            .Distinct(StringComparer.Ordinal)
            .Take(MaxReasons)
            .ToList();
        var last = months[^1].PeriodEnd;
        return new InvestmentHistoryYearDto(
            year,
            last,
            last.Month == 12,
            period.Opening,
            period.Closing,
            period.Contributions,
            Sum(totals.Select(value => value.Dividends)),
            Sum(totals.Select(value => value.RealisedProfitLoss)),
            period.Growth,
            period.ReturnPercent,
            period.IsComplete,
            reasons,
            Segments(months, segmentNames));
    }

    private static InvestmentHistoryLifetimeDto Lifetime(IReadOnlyList<MonthRows> months, IReadOnlyList<string> segmentNames)
    {
        if (months.Count == 0)
            return new InvestmentHistoryLifetimeDto(null, null, null, null, null, null, null, null, null, true, []);
        var totals = months.Select(month => month.Total!).ToList();
        var period = Period(totals);
        decimal? annualised = null;
        if (months.Count >= 12 && MonthlyMoneyWeightedRate(totals) is { } monthly)
            annualised = ToPercent((decimal)(Math.Pow(1 + monthly, 12) - 1));
        return new InvestmentHistoryLifetimeDto(
            months[0].PeriodEnd,
            months[^1].PeriodEnd,
            period.Contributions,
            period.Closing,
            period.Growth,
            Sum(totals.Select(value => value.Dividends)),
            Sum(totals.Select(value => value.RealisedProfitLoss)),
            period.ReturnPercent,
            annualised,
            period.IsComplete,
            Segments(months, segmentNames));
    }

    private static IReadOnlyList<InvestmentHistorySegmentDto> Segments(IReadOnlyList<MonthRows> months, IReadOnlyList<string> segmentNames)
    {
        var segments = new List<InvestmentHistorySegmentDto>();
        foreach (var name in segmentNames)
        {
            // Before a market's first holding it has no row: it was worth nothing and nothing moved.
            var rows = months.Select(month => month.Segments.GetValueOrDefault(name) ?? Empty(month.PeriodEnd, name)).ToList();
            if (rows.All(value => value.ClosingValue == 0 && value.NetContributions == 0 && value.OpeningValue == 0)) continue;
            var period = Period(rows);
            segments.Add(new InvestmentHistorySegmentDto(
                name, period.Opening, period.Closing, period.Contributions, period.Growth, period.ReturnPercent, period.IsComplete));
        }
        return segments;
    }

    private static PeriodFigures Period(IReadOnlyList<InvestmentPeriodSnapshot> rows)
    {
        var opening = rows[0].OpeningValue;
        var closing = rows[^1].ClosingValue;
        var contributions = Sum(rows.Select(value => value.NetContributions));
        var growth = closing - opening - contributions;
        // Withdrawals do not shrink what was at stake: only money added counts.
        var atStake = opening + rows.Aggregate((decimal?)0, (sum, value) => sum + (value.NetContributions is { } added ? Math.Max(added, 0) : null));
        decimal? returnPercent = null;
        if (growth is { } known && atStake is { } stake)
            returnPercent = stake > 0 ? ToPercent(known / stake) : known == 0 ? 0 : null;
        return new PeriodFigures(opening, closing, contributions, growth, returnPercent, rows.All(value => value.IsComplete));
    }

    /// <summary>
    /// The monthly rate r at which the opening value and every month's money, each compounding from
    /// when it arrived, grows into the closing value. Solved by bisection; null when a figure is
    /// unknown or no rate in range fits.
    /// </summary>
    private static double? MonthlyMoneyWeightedRate(IReadOnlyList<InvestmentPeriodSnapshot> rows)
    {
        if (rows[0].OpeningValue is not { } opening || rows[^1].ClosingValue is not { } closing) return null;
        var count = rows.Count;
        var flows = new List<(double Amount, double Months)>();
        for (var index = 0; index < count; index++)
        {
            if (rows[index].NetContributions is not { } amount) return null;
            if (amount == 0) continue;
            // The share of its own month the money was invested for; mid-month when unrecorded.
            var share = rows[index].WeightedContributions is { } weighted ? (double)(weighted / amount) : 0.5;
            flows.Add(((double)amount, count - 1 - index + Math.Clamp(share, 0, 1)));
        }
        double Excess(double rate) => (double)opening * Math.Pow(1 + rate, count)
            + flows.Sum(flow => flow.Amount * Math.Pow(1 + rate, flow.Months)) - (double)closing;
        double low = -0.99, high = 1;
        if (Math.Sign(Excess(low)) == Math.Sign(Excess(high))) return null;
        for (var step = 0; step < 200; step++)
        {
            var middle = (low + high) / 2;
            if (Math.Sign(Excess(middle)) == Math.Sign(Excess(low))) low = middle; else high = middle;
        }
        return (low + high) / 2;
    }

    /// <summary>One plus the month's Modified Dietz return, or null when it cannot be known.</summary>
    private static decimal? MonthFactor(InvestmentPeriodSnapshot row)
    {
        if (row.OpeningValue is not { } opening || row.ClosingValue is not { } closing ||
            row.NetContributions is not { } contributions)
            return null;
        var growth = closing - opening - contributions;
        var capital = opening + (row.WeightedContributions ?? contributions / 2);
        // Money that arrived on the month's last day carries no weight; fall back to the
        // mid-month convention rather than divide by nothing.
        if (capital <= 0) capital = opening + contributions / 2;
        if (capital > 0) return 1 + growth / capital;
        // Nothing invested at all: an idle month neither gains nor loses.
        return growth == 0 ? 1 : null;
    }

    private static InvestmentPeriodSnapshot Empty(DateOnly periodEnd, string segment) => new()
    {
        PeriodEnd = periodEnd,
        Segment = segment,
        OpeningValue = 0,
        ClosingValue = 0,
        NetContributions = 0,
        WeightedContributions = 0,
        IsComplete = true,
    };

    private static decimal? Sum(IEnumerable<decimal?> values)
        => values.Aggregate((decimal?)0, (sum, value) => sum + value);

    private static decimal? ToPercent(decimal? fraction)
        => fraction is null ? null : Math.Round(fraction.Value * 100, 4, MidpointRounding.AwayFromZero);

    private sealed record MonthRows(
        DateOnly PeriodEnd,
        InvestmentPeriodSnapshot? Total,
        IReadOnlyDictionary<string, InvestmentPeriodSnapshot> Segments);

    private sealed record PeriodFigures(
        decimal? Opening,
        decimal? Closing,
        decimal? Contributions,
        decimal? Growth,
        decimal? ReturnPercent,
        bool IsComplete);
}
