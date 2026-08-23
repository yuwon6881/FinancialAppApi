using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class FinancialService
{
    private sealed record PendingRecurringItem(
        string RecurringPaymentId,
        string Category,
        string LedgerCategory,
        decimal Amount);

    private static List<PendingRecurringItem> BuildPendingRecurringItems(
        List<RecurringPaymentOccurrence> occurrences,
        IReadOnlyCollection<string> activePaymentIds,
        IReadOnlyCollection<Transaction>? transactions = null)
    {
        var nonDiscardedTxs = transactions?
            .Where(t => t.RecurringPaymentId != null && t.RecurringOccurrenceDate != null && !string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => (PaymentId: t.RecurringPaymentId!, Date: t.RecurringOccurrenceDate!.Value))
            .ToDictionary(g => g.Key, g => g.Sum(t => Math.Abs(t.Amount))) ?? new();

        return occurrences
            .Where(occurrence => (occurrence.Status == RecurringOccurrenceStatus.Pending || occurrence.Status == RecurringOccurrenceStatus.PartiallyPaid)
                && occurrence.ScheduledAmount.HasValue
                && activePaymentIds.Contains(occurrence.RecurringPaymentId))
            .Select(occurrence =>
            {
                var scheduled = Math.Abs(occurrence.ScheduledAmount!.Value);
                var paid = nonDiscardedTxs.GetValueOrDefault((occurrence.RecurringPaymentId, occurrence.OccurrenceDate));
                var remaining = Math.Max(0m, scheduled - paid);
                return new PendingRecurringItem(
                    occurrence.RecurringPaymentId,
                    occurrence.Category ?? string.Empty,
                    occurrence.LedgerCategory ?? string.Empty,
                    remaining);
            })
            .Where(item => item.Amount > 0m)
            .ToList();
    }

    private object BuildTodayPlanInsights(
        List<Transaction> activeCycleTxs,
        List<PendingRecurringItem> pendingRecurring,
        decimal essentialsRemaining,
        decimal unpaidEssentials,
        DateTime rangeStart,
        DateTime rangeEnd)
    {
        var nonRecurringEssentialsSpent = Math.Abs(activeCycleTxs
            .Where(transaction =>
                transaction.Amount < 0 &&
                TransactionReportSemantics.IsReportableOutflow(transaction) &&
                string.IsNullOrWhiteSpace(transaction.RecurringPaymentId) &&
                !string.Equals(transaction.Category, "Adjustment", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(transaction.LedgerCategory, "Essentials", StringComparison.OrdinalIgnoreCase))
            .Sum(transaction => transaction.Amount));

        var start = DateOnly.FromDateTime(rangeStart);
        var end = DateOnly.FromDateTime(rangeEnd);
        var today = _financialClock.Today;
        var totalDays = end.DayNumber - start.DayNumber + 1;
        var elapsedDays = today < start
            ? 0
            : today > end
                ? totalDays
                : today.DayNumber - start.DayNumber + 1;
        var remainingDaysAfterToday = today < start
            ? totalDays
            : today > end
                ? 0
                : Math.Max(0, totalDays - elapsedDays);
        var dailyAverage = elapsedDays > 0 ? nonRecurringEssentialsSpent / elapsedDays : 0m;
        var projectedEndingBalance = today > end
            ? essentialsRemaining
            : essentialsRemaining - unpaidEssentials - (dailyAverage * remainingDaysAfterToday);

        return new
        {
            unpaidRecurringCount = pendingRecurring.Count,
            unpaidRecurringTotal = ObfuscationHelper.Obfuscate(pendingRecurring.Sum(item => item.Amount)),
            unpaidEssentialsTotal = ObfuscationHelper.Obfuscate(unpaidEssentials),
            nonRecurringEssentialsSpent = ObfuscationHelper.Obfuscate(nonRecurringEssentialsSpent),
            nonRecurringEssentialsDailyAverage = ObfuscationHelper.Obfuscate(dailyAverage),
            projectedEssentialsEndingBalance = ObfuscationHelper.Obfuscate(projectedEndingBalance)
        };
    }

    private async Task<List<object>> BuildCategoryLimitProgressAsync(
        List<Transaction> activeCycleTxs,
        List<PendingRecurringItem> pendingRecurring,
        int activeYear,
        int activeMonthIndex,
        int cycleDay,
        DateTime rangeStart,
        DateTime rangeEnd,
        CancellationToken cancellationToken)
    {
        var cycleKey = $"{activeYear:D4}-{activeMonthIndex:D2}";
        var (currentCycleYear, currentCycleMonthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            _financialClock.Today,
            cycleDay);
        var currentCycleKey = $"{currentCycleYear:D4}-{currentCycleMonthIndex:D2}";
        var categoryTypes = (await _context.TransactionCategories
                .AsNoTracking()
                .Select(category => new { category.Name, category.Type })
                .ToListAsync(cancellationToken))
            .GroupBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Type, StringComparer.OrdinalIgnoreCase);
        var allGuideVersions = await _context.CategorySpendingGuides
            .AsNoTracking()
            .Where(guide => string.Compare(guide.EffectiveFromCycleKey, cycleKey) <= 0)
            .ToListAsync(cancellationToken);
        var effectiveGuides = allGuideVersions
            .GroupBy(guide => guide.CategoryName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(guide => guide.EffectiveFromCycleKey).First())
            .Where(guide => guide.LimitAmount.HasValue)
            // A current/future guide is valid only while its category accepts outflows.
            // Earlier cycles retain their historical guide even if the category later changes
            // flow type or is removed.
            .Where(guide => string.CompareOrdinal(guide.EffectiveFromCycleKey, currentCycleKey) < 0 ||
                (categoryTypes.TryGetValue(guide.CategoryName, out var type) &&
                 CategoryFlowType.AllowsSpendingGuide(type)))
            .OrderBy(guide => guide.CategoryName)
            .ToList();

        if (effectiveGuides.Count == 0) return [];

        var expenseTransactions = activeCycleTxs
            .Where(transaction =>
                transaction.Amount < 0 &&
                TransactionReportSemantics.IsReportableOutflow(transaction))
            .ToList();
        var spentByCategory = expenseTransactions
            .GroupBy(transaction => transaction.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Math.Abs(group.Sum(transaction => transaction.Amount)), StringComparer.OrdinalIgnoreCase);
        var recurringSpentByCategory = expenseTransactions
            .Where(transaction => !string.IsNullOrWhiteSpace(transaction.RecurringPaymentId))
            .GroupBy(transaction => transaction.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Math.Abs(group.Sum(transaction => transaction.Amount)), StringComparer.OrdinalIgnoreCase);
        var nonRecurringSpentByCategory = expenseTransactions
            .Where(transaction => string.IsNullOrWhiteSpace(transaction.RecurringPaymentId))
            .GroupBy(transaction => transaction.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Math.Abs(group.Sum(transaction => transaction.Amount)), StringComparer.OrdinalIgnoreCase);
        var pendingByCategory = pendingRecurring
            .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Amount), StringComparer.OrdinalIgnoreCase);

        var start = DateOnly.FromDateTime(rangeStart);
        var end = DateOnly.FromDateTime(rangeEnd);
        var today = _financialClock.Today;
        var totalDays = end.DayNumber - start.DayNumber + 1;
        var elapsedDays = today < start
            ? 0
            : today > end
                ? totalDays
                : today.DayNumber - start.DayNumber + 1;
        var isEnded = today > end;

        return effectiveGuides.Select(guide =>
        {
            var limit = guide.LimitAmount!.Value;
            var spent = spentByCategory.GetValueOrDefault(guide.CategoryName);
            var recurringSpent = recurringSpentByCategory.GetValueOrDefault(guide.CategoryName);
            var nonRecurringSpent = nonRecurringSpentByCategory.GetValueOrDefault(guide.CategoryName);
            var pending = pendingByCategory.GetValueOrDefault(guide.CategoryName);
            var projected = isEnded
                ? spent
                : recurringSpent + pending + (elapsedDays > 0 ? nonRecurringSpent / elapsedDays * totalDays : 0m);
            var status = spent > limit
                ? "Exceeded"
                : projected > limit
                    ? "Watch"
                    : "OnTrack";

            return (object)new
            {
                category = guide.CategoryName,
                limit = ObfuscationHelper.Obfuscate(limit),
                spent = ObfuscationHelper.Obfuscate(spent),
                remaining = ObfuscationHelper.Obfuscate(limit - spent),
                pendingCommitted = ObfuscationHelper.Obfuscate(pending),
                projectedSpend = ObfuscationHelper.Obfuscate(projected),
                percentUsed = limit > 0 ? (double)(spent / limit) : 0d,
                status
            };
        }).ToList();
    }
}
