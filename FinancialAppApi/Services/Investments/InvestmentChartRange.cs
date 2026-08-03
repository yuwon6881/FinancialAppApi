namespace FinancialAppApi.Services.Investments;

/// <summary>
/// The one place a chart range is interpreted. Both the portfolio chart and the
/// per-fund history read from here so a range accepted by the controller can
/// never mean something different to one of them.
/// </summary>
public static class InvestmentChartRange
{
    /// <summary>Charted output is capped at this many points regardless of range.</summary>
    public const int MaxPoints = 180;

    public static readonly string[] Allowed = ["1m", "3m", "6m", "1y", "3y", "5y", "all"];

    public static bool IsAllowed(string range)
        => Allowed.Contains(range, StringComparer.OrdinalIgnoreCase);

    /// <summary>The first date in range, or null for "all" (caller supplies its own earliest date).</summary>
    public static DateOnly? StartFor(string range, DateOnly today) => range.ToLowerInvariant() switch
    {
        "1m" => today.AddMonths(-1),
        "3m" => today.AddMonths(-3),
        "6m" => today.AddMonths(-6),
        "1y" => today.AddYears(-1),
        "3y" => today.AddYears(-3),
        "5y" => today.AddYears(-5),
        _ => null,
    };

    /// <summary>
    /// Thins an ordered series to <see cref="MaxPoints"/>, always keeping the last
    /// entry: the newest point is the one the reader actually looks at.
    /// </summary>
    public static List<T> Sample<T>(List<T> values)
    {
        if (values.Count <= MaxPoints) return values;
        var interval = (int)Math.Ceiling(values.Count / (decimal)MaxPoints);
        var sampled = values.Where((_, index) => index % interval == 0).ToList();
        if (!ReferenceEquals(sampled[^1], values[^1]) && !Equals(sampled[^1], values[^1]))
            sampled.Add(values[^1]);
        return sampled;
    }
}
