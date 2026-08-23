using System.Globalization;
using System.Text.RegularExpressions;
using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private static bool IsTransfer(AiTransactionRow transaction) =>
        transaction.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ||
        transaction.Category.Equals("Transfer", StringComparison.OrdinalIgnoreCase);

    private static bool IsInCycle(AiTransactionRow transaction, CycleKey cycle, int cycleDay)
    {
        var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
        var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
        var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
        return transaction.Timestamp >= start && transaction.Timestamp < end;
    }

    private static IReadOnlyList<TransactionDateRange> MergeCycleRanges(IReadOnlyList<CycleKey> cycles, int cycleDay)
    {
        var ranges = cycles
            .Distinct()
            .Select(cycle => CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay))
            .Select(range => new TransactionDateRange(
                TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start)),
                TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end))))
            .OrderBy(range => range.Start)
            .ToList();
        if (ranges.Count <= 1) return ranges;

        var merged = new List<TransactionDateRange> { ranges[0] };
        foreach (var range in ranges.Skip(1))
        {
            var previous = merged[^1];
            if (range.Start <= previous.End)
            {
                merged[^1] = previous with { End = range.End > previous.End ? range.End : previous.End };
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }

    private static TargetCycleSelection ResolveTargetCycles(
        string queryText,
        int selectedYear,
        int selectedMonthIndex,
        bool needsCycleSummary,
        bool needsComparison)
    {
        var explicitCycles = Regex.Matches(
                queryText,
                $@"\b(?<month>{MonthNamePattern})\s+(?<year>(?:19|20)\d{{2}})\b",
                RegexOptions.IgnoreCase)
            .Select(match => new CycleKey(
                int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
                GetMonthNumber(match.Groups["month"].Value)))
            .Distinct()
            .Take(12)
            .ToList();
        explicitCycles.AddRange(Regex.Matches(queryText, @"\b(?<year>(?:19|20)\d{2})-(?<month>0?[1-9]|1[0-2])\b")
            .Select(match => new CycleKey(
                int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture))));
        explicitCycles = explicitCycles.Distinct().Take(24).ToList();
        if (explicitCycles.Count > 0)
        {
            if (explicitCycles.Count == 2 &&
                Regex.IsMatch(queryText, @"\b(between|through|until|from)\b|\bto\b", RegexOptions.IgnoreCase))
            {
                explicitCycles = ExpandCycleRange(explicitCycles[0], explicitCycles[1], 24);
            }
            return new TargetCycleSelection(explicitCycles, true);
        }

        // All-history is a real database scope, not a synthetic trailing-cycle window. Purchase
        // cadence defaults to it because a frequency estimate from only the active cycle is not
        // meaningful; an explicitly named cycle/range above still wins.
        if (AllHistorySignal.IsMatch(queryText) || PurchaseFrequencySignal.IsMatch(queryText))
        {
            return new TargetCycleSelection([], true, AllHistory: true);
        }

        var wholeYear = Regex.Match(
            queryText,
            @"\b(?:in|during|for|year)\s+(?<year>(?:19|20)\d{2})\b",
            RegexOptions.IgnoreCase);
        if (wholeYear.Success)
        {
            var year = int.Parse(wholeYear.Groups["year"].Value, CultureInfo.InvariantCulture);
            return new TargetCycleSelection(
                Enumerable.Range(1, 12).Select(month => new CycleKey(year, month)).ToList(),
                true);
        }

        if (Regex.IsMatch(queryText, @"\b(last|previous|prior)\s+year\b", RegexOptions.IgnoreCase))
        {
            return new TargetCycleSelection(
                Enumerable.Range(1, 12).Select(month => new CycleKey(selectedYear - 1, month)).ToList(),
                true);
        }
        if (Regex.IsMatch(queryText, @"\b(this|current)\s+year\b", RegexOptions.IgnoreCase))
        {
            return new TargetCycleSelection(
                Enumerable.Range(1, 12).Select(month => new CycleKey(selectedYear, month)).ToList(),
                true);
        }

        var quarter = Regex.Match(queryText,
            @"\b(?:q(?<number>[1-4])|(?<word>first|second|third|fourth) quarter)(?:\s+(?<year>(?:19|20)\d{2}))?\b",
            RegexOptions.IgnoreCase);
        if (quarter.Success)
        {
            var number = quarter.Groups["number"].Success
                ? int.Parse(quarter.Groups["number"].Value, CultureInfo.InvariantCulture)
                : quarter.Groups["word"].Value.ToLowerInvariant() switch
                {
                    "first" => 1, "second" => 2, "third" => 3, _ => 4
                };
            var year = quarter.Groups["year"].Success
                ? int.Parse(quarter.Groups["year"].Value, CultureInfo.InvariantCulture)
                : selectedYear;
            return new TargetCycleSelection(
                Enumerable.Range((number - 1) * 3 + 1, 3).Select(month => new CycleKey(year, month)).ToList(),
                true);
        }

        var relativeQuarter = Regex.Match(queryText, @"\b(?<which>this|current|last|previous|prior|next) quarter\b", RegexOptions.IgnoreCase);
        if (relativeQuarter.Success)
        {
            var activeOrdinal = selectedYear * 12 + selectedMonthIndex - 1;
            var activeQuarterStart = activeOrdinal - ((selectedMonthIndex - 1) % 3);
            var shift = relativeQuarter.Groups["which"].Value.ToLowerInvariant() switch
            {
                "last" or "previous" or "prior" => -3,
                "next" => 3,
                _ => 0
            };
            return new TargetCycleSelection(
                Enumerable.Range(activeQuarterStart + shift, 3)
                    .Select(ordinal => new CycleKey(ordinal / 12, ordinal % 12 + 1)).ToList(),
                true);
        }

        var cyclesAgo = Regex.Match(queryText, @"\b(?<count>\d{1,2})\s+(?:cycles?|months?)\s+ago\b", RegexOptions.IgnoreCase);
        if (cyclesAgo.Success)
        {
            var offset = -Math.Clamp(int.Parse(cyclesAgo.Groups["count"].Value, CultureInfo.InvariantCulture), 1, 120);
            var target = AddMonths(selectedYear, selectedMonthIndex, offset);
            return new TargetCycleSelection([new CycleKey(target.Year, target.MonthIndex)], true);
        }
        if (Regex.IsMatch(queryText, @"\b(?:cycle|month)\s+before\s+last\b", RegexOptions.IgnoreCase))
        {
            var target = AddMonths(selectedYear, selectedMonthIndex, -2);
            return new TargetCycleSelection([new CycleKey(target.Year, target.MonthIndex)], true);
        }

        var relativeCount = Regex.Match(
            queryText,
            @"\b(?:last|past|previous|prior)\s+(?<count>\d{1,2}|few)\s+(?:cycles?|months?)\b",
            RegexOptions.IgnoreCase);
        if (relativeCount.Success)
        {
            var count = relativeCount.Groups["count"].Value.Equals("few", StringComparison.OrdinalIgnoreCase)
                ? 3
                : Math.Clamp(int.Parse(relativeCount.Groups["count"].Value, CultureInfo.InvariantCulture), 1, 12);
            return new TargetCycleSelection(
                Enumerable.Range(1, count)
                    .Select(offset => AddMonths(selectedYear, selectedMonthIndex, -offset))
                    .Select(value => new CycleKey(value.Year, value.MonthIndex))
                    .ToList(),
                true);
        }

        var cycles = new List<CycleKey>();
        if (Regex.IsMatch(queryText, @"\b(this|current)\s+(cycle|month)\b", RegexOptions.IgnoreCase))
        {
            cycles.Add(new CycleKey(selectedYear, selectedMonthIndex));
        }
        if (Regex.IsMatch(queryText, @"\b(previous|prior|last)\s+(cycle|month)\b", RegexOptions.IgnoreCase))
        {
            var previous = AddMonths(selectedYear, selectedMonthIndex, -1);
            cycles.Add(new CycleKey(previous.Year, previous.MonthIndex));
            if (needsComparison) cycles.Add(new CycleKey(selectedYear, selectedMonthIndex));
        }
        if (Regex.IsMatch(queryText, @"\b(next|following)\s+(cycle|month)\b", RegexOptions.IgnoreCase))
        {
            var next = AddMonths(selectedYear, selectedMonthIndex, 1);
            cycles.Add(new CycleKey(next.Year, next.MonthIndex));
            if (needsComparison) cycles.Add(new CycleKey(selectedYear, selectedMonthIndex));
        }
        if (cycles.Count > 0) return new TargetCycleSelection(cycles.Distinct().ToList(), true);

        var namedMonth = Regex.Match(
            queryText,
            $@"(?:\b(?:cycle|month|in|for|about|during|and|vs\.?|versus|with|against)\s+|^\s*(?:(?:and|also|then|now|what about|how about|instead|just)\s+)?)(?<month>{MonthNamePattern})\b",
            RegexOptions.IgnoreCase);
        if (namedMonth.Success)
        {
            return new TargetCycleSelection(
                [new CycleKey(selectedYear, GetMonthNumber(namedMonth.Groups["month"].Value))],
                true);
        }

        if (!needsCycleSummary) return new TargetCycleSelection([], false);
        if (!needsComparison)
        {
            return new TargetCycleSelection([new CycleKey(selectedYear, selectedMonthIndex)], false);
        }

        return new TargetCycleSelection(
            Enumerable.Range(-6, 7)
                .Select(offset => AddMonths(selectedYear, selectedMonthIndex, offset))
                .Select(value => new CycleKey(value.Year, value.MonthIndex))
                .ToList(),
            false);
    }

    private static List<CycleKey> ExpandCycleRange(CycleKey first, CycleKey second, int maximum)
    {
        var firstOrdinal = first.Year * 12 + first.MonthIndex - 1;
        var secondOrdinal = second.Year * 12 + second.MonthIndex - 1;
        var start = Math.Min(firstOrdinal, secondOrdinal);
        var end = Math.Max(firstOrdinal, secondOrdinal);
        return Enumerable.Range(start, Math.Min(end - start + 1, maximum))
            .Select(ordinal => new CycleKey(ordinal / 12, ordinal % 12 + 1))
            .ToList();
    }

    private static (int Year, int MonthIndex) AddMonths(int year, int monthIndex, int offset)
    {
        var zeroBased = (monthIndex - 1) + offset;
        year += (int)Math.Floor(zeroBased / 12.0);
        var month = ((zeroBased % 12) + 12) % 12 + 1;
        return (year, month);
    }
}
