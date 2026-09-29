using System.Globalization;
using FinancialAppApi.Database;

namespace FinancialAppApi.Services.AI;

// A financial cycle, named by the month it is labelled with. A cycle can span two calendar months
// (it starts on the cycle day), so the label is never derived from a transaction's calendar month.
public readonly record struct AiCycle(int Year, int MonthIndex)
{
    public string Key => $"{Year:D4}-{MonthIndex:D2}";

    public AiCycle AddCycles(int count)
    {
        var ordinal = Year * 12 + (MonthIndex - 1) + count;
        return new AiCycle(ordinal / 12, ordinal % 12 + 1);
    }
}

// Half-open [Start, End) timestamps covering whole transaction dates.
public readonly record struct AiDateRange(DateTime Start, DateTime End)
{
    public DateOnly FirstDate => DateOnly.FromDateTime(Start);
    public DateOnly LastDate => DateOnly.FromDateTime(End).AddDays(-1);

    public static AiDateRange FromDates(DateOnly first, DateOnly last) =>
        new(TransactionDate.StartOfDate(first), TransactionDate.ExclusiveEndOfDate(last));
}

// The single place tools turn cycle words and keys into date ranges. All math delegates to
// CategoryAttributionService so the assistant agrees with the Ledger and Reports screens.
public static class AiCycleResolver
{
    public static AiCycle CycleOf(DateOnly date, int cycleDay)
    {
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(date, cycleDay);
        return new AiCycle(year, monthIndex);
    }

    public static AiCycle Current(DateOnly today, int cycleDay) => CycleOf(today, cycleDay);

    public static AiDateRange Range(AiCycle cycle, int cycleDay)
    {
        var (start, end, _) = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
        return AiDateRange.FromDates(DateOnly.FromDateTime(start), DateOnly.FromDateTime(end));
    }

    public static string Label(AiCycle cycle, int cycleDay) =>
        CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay).label;

    // Accepts "current", "previous", or a "yyyy-MM" key. The error lists what is accepted so the
    // model can correct itself on the next round.
    public static AiCycle Parse(string token, DateOnly today, int cycleDay)
    {
        var current = Current(today, cycleDay);
        var text = token.Trim();
        if (text.Equals("current", StringComparison.OrdinalIgnoreCase)) return current;
        if (text.Equals("previous", StringComparison.OrdinalIgnoreCase)) return current.AddCycles(-1);
        if (text.Length == 7 && text[4] == '-' &&
            int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
            int.TryParse(text.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
            year is >= 1900 and <= 2100 && month is >= 1 and <= 12)
        {
            return new AiCycle(year, month);
        }
        throw new Tools.AiToolArgumentException(
            $"Unknown cycle '{token}'. Use \"current\", \"previous\", or a cycle key like {current.Key}.");
    }

    // Adjacent cycles are merged so one contiguous stretch becomes one range predicate.
    public static IReadOnlyList<AiDateRange> MergedRanges(IEnumerable<AiCycle> cycles, int cycleDay)
    {
        var ranges = cycles.Distinct().Select(cycle => Range(cycle, cycleDay)).OrderBy(range => range.Start).ToList();
        var merged = new List<AiDateRange>();
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                var previous = merged[^1];
                merged[^1] = previous with { End = range.End > previous.End ? range.End : previous.End };
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }
}
