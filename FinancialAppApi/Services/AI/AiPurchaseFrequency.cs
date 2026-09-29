namespace FinancialAppApi.Services;

// Pure cadence arithmetic for a repeated purchase, used by the get_purchase_pattern tool.
public partial class AiAssistantService
{
    internal sealed record AiPurchaseMatch(string Id, DateOnly Date, string Description);

    internal sealed record AiPurchaseFrequencyMetric(
        string Query,
        string MatchMode,
        int TransactionCount,
        int PurchaseDayCount,
        string? FirstPurchaseDate,
        string? LastPurchaseDate,
        decimal? MedianGapDays,
        decimal PurchaseDaysPerCycle,
        int ObservedCycleCount,
        int? DaysSinceLastPurchase,
        string? TypicalCadence,
        string? NextExpectedDate,
        int? DaysUntilNextExpected,
        bool NextExpectedIsOverdue,
        string? SearchedFrom,
        string SearchedThrough,
        IReadOnlyList<string> SampleDescriptions,
        string Confidence);

    internal static AiPurchaseFrequencyMetric BuildPurchaseFrequencyMetric(
        string query,
        string matchMode,
        IReadOnlyList<AiPurchaseMatch> matches,
        DateOnly? scopeStart,
        DateOnly scopeEnd,
        DateOnly today,
        int cycleDay)
    {
        var purchaseDays = matches.Select(match => match.Date).Distinct().Order().ToList();
        var gaps = purchaseDays.Zip(purchaseDays.Skip(1), (first, second) => second.DayNumber - first.DayNumber)
            .Order()
            .ToList();
        decimal? medianGap = gaps.Count == 0
            ? null
            : gaps.Count % 2 == 1
                ? gaps[gaps.Count / 2]
                : (gaps[gaps.Count / 2 - 1] + gaps[gaps.Count / 2]) / 2m;
        var observedCycles = scopeStart.HasValue ? CountCycles(scopeStart.Value, scopeEnd, cycleDay) : 0;
        var purchaseDaysPerCycle = observedCycles == 0
            ? 0m
            : decimal.Round(purchaseDays.Count / (decimal)observedCycles, 2, MidpointRounding.AwayFromZero);
        var last = purchaseDays.Count > 0 ? purchaseDays[^1] : (DateOnly?)null;
        // "When is the next one due" is the other half of a cadence question, so the projection is
        // derived here: the prompt forbids the model from doing cadence arithmetic itself, which
        // left "estimate the next one" unanswerable even when the history was right there. A
        // projected date already in the past is reported as overdue rather than rolled silently
        // forward -- being late is the honest answer in that case.
        var nextExpected = last.HasValue && medianGap.HasValue
            ? last.Value.AddDays((int)decimal.Round(medianGap.Value, 0, MidpointRounding.AwayFromZero))
            : (DateOnly?)null;

        return new AiPurchaseFrequencyMetric(
            query,
            matches.Count == 0 ? "none" : matchMode,
            matches.Count,
            purchaseDays.Count,
            purchaseDays.FirstOrDefault() == default ? null : purchaseDays[0].ToString("yyyy-MM-dd"),
            last?.ToString("yyyy-MM-dd"),
            medianGap,
            purchaseDaysPerCycle,
            observedCycles,
            last.HasValue ? Math.Max(0, today.DayNumber - last.Value.DayNumber) : null,
            FormatCadence(medianGap),
            nextExpected?.ToString("yyyy-MM-dd"),
            nextExpected.HasValue ? nextExpected.Value.DayNumber - today.DayNumber : null,
            nextExpected.HasValue && nextExpected.Value < today,
            scopeStart?.ToString("yyyy-MM-dd"),
            scopeEnd.ToString("yyyy-MM-dd"),
            matches.Select(match => match.Description).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList(),
            purchaseDays.Count switch { 0 => "none", 1 => "insufficient", _ => "usable" });
    }

    private static int CountCycles(DateOnly start, DateOnly end, int cycleDay)
    {
        var first = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(start, cycleDay);
        var last = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(end, cycleDay);
        var firstOrdinal = first.year * 12 + first.monthIndex - 1;
        var lastOrdinal = last.year * 12 + last.monthIndex - 1;
        return Math.Max(1, lastOrdinal - firstOrdinal + 1);
    }

    private static string? FormatCadence(decimal? medianGapDays)
    {
        if (!medianGapDays.HasValue) return null;
        if (medianGapDays.Value < 14m)
        {
            var days = Math.Max(1, decimal.Round(medianGapDays.Value, 0, MidpointRounding.AwayFromZero));
            return $"about every {days:0} {(days == 1m ? "day" : "days")}";
        }
        if (medianGapDays.Value < 60m)
        {
            var weeks = decimal.Round(medianGapDays.Value / 7m, 1, MidpointRounding.AwayFromZero);
            return $"about every {weeks:0.#} {(weeks == 1m ? "week" : "weeks")}";
        }

        var months = decimal.Round(medianGapDays.Value / 30.44m, 1, MidpointRounding.AwayFromZero);
        return $"about every {months:0.#} {(months == 1m ? "month" : "months")}";
    }
}
