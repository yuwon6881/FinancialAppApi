using FinancialAppApi.Models;

namespace FinancialAppApi.Services.AI;

public sealed record AiSpendRow(DateOnly Date, string Category, string LedgerCategory, decimal Amount, string? RecurringPaymentId);

public sealed record AiCategoryLimitProgress(
    string Cycle,
    string Category,
    decimal Limit,
    decimal Spent,
    decimal Remaining,
    decimal PendingCommitted,
    decimal ProjectedSpend,
    double PercentUsed,
    // OnTrack | Watch (projected to exceed) | Exceeded
    string Status,
    bool IsComplete,
    string? ObservedThrough);

// Effective-dated category-limit progress. A limit version applies from its cycle onward; the
// projection for an unfinished cycle adds bills still owed (at what they still owe) and the
// non-recurring pace so far, stretched over the whole cycle.
public static class AiCategoryLimitCalculator
{
    public static IReadOnlyList<AiCategoryLimitProgress> Compute(
        IReadOnlyList<CategorySpendingGuide> guideVersions,
        IReadOnlyDictionary<string, string> categoryTypes,
        IReadOnlyList<AiBillStatus> billStatuses,
        IReadOnlyList<AiCycle> cycles,
        int cycleDay,
        DateOnly today,
        IReadOnlyList<AiSpendRow> rows)
    {
        var currentCycleKey = AiCycleResolver.Current(today, cycleDay).Key;
        var results = new List<AiCategoryLimitProgress>();
        foreach (var cycle in cycles)
        {
            var key = cycle.Key;
            var range = AiCycleResolver.Range(cycle, cycleDay);
            var start = range.FirstDate;
            var end = range.LastDate;
            var effectiveGuides = guideVersions
                .Where(guide => string.CompareOrdinal(guide.EffectiveFromCycleKey, key) <= 0)
                .GroupBy(guide => guide.CategoryName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(guide => guide.EffectiveFromCycleKey).First())
                .Where(guide => guide.LimitAmount.HasValue)
                .Where(guide => string.CompareOrdinal(guide.EffectiveFromCycleKey, currentCycleKey) < 0 ||
                    (categoryTypes.TryGetValue(guide.CategoryName, out var type) &&
                     CategoryFlowType.AllowsSpendingGuide(type)))
                .OrderBy(guide => guide.CategoryName)
                .ToList();

            var totalDays = end.DayNumber - start.DayNumber + 1;
            var elapsedDays = today < start ? 0 : today > end ? totalDays : today.DayNumber - start.DayNumber + 1;
            var isComplete = today > end;
            var observedThrough = today < start ? null : (today < end ? today : end).ToString("yyyy-MM-dd");

            foreach (var guide in effectiveGuides)
            {
                var categoryRows = rows
                    .Where(row => row.Date >= start && row.Date <= end && row.Amount < 0 &&
                        TransactionReportSemantics.IsReportableCashMovement(row.Amount, row.Category, row.LedgerCategory) &&
                        string.Equals(row.Category, guide.CategoryName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var spent = Math.Abs(categoryRows.Sum(row => row.Amount));
                var recurringSpent = Math.Abs(categoryRows
                    .Where(row => !string.IsNullOrWhiteSpace(row.RecurringPaymentId))
                    .Sum(row => row.Amount));
                // Every bill that still owes something, counted at what it still owes: keyed on
                // "Pending" alone a partly paid bill dropped out, and counted at its full amount it
                // projected the part already spent a second time.
                var pending = billStatuses
                    .Where(item => RecurringOccurrenceStatus.IsUnresolved(item.Status) &&
                        DateOnly.TryParse(item.DueDate, out var dueDate) && dueDate >= start && dueDate <= end &&
                        string.Equals(item.Category, guide.CategoryName, StringComparison.OrdinalIgnoreCase))
                    .Sum(item => item.Outstanding);
                var nonRecurringSpent = Math.Max(0, spent - recurringSpent);
                var limit = guide.LimitAmount!.Value;
                var projected = isComplete
                    ? spent
                    : recurringSpent + pending + (elapsedDays > 0 ? nonRecurringSpent / elapsedDays * totalDays : 0m);
                var status = spent > limit ? "Exceeded" : projected > limit ? "Watch" : "OnTrack";

                results.Add(new AiCategoryLimitProgress(
                    key, guide.CategoryName, limit, spent, limit - spent, pending, projected,
                    limit > 0 ? (double)(spent / limit) : 0d, status, isComplete, observedThrough));
            }
        }
        return results;
    }
}
