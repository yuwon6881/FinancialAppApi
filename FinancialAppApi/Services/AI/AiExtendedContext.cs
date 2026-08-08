using System.Text.RegularExpressions;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

// Optional, intent-gated context for newer product areas. Keeping these calculations outside
// AiAssistantService.Context leaves the main orchestration as a short wiring layer.
public partial class AiAssistantService
{
    private async Task<object?> BuildCategoryLimitContextAsync(
        AiQueryPlan queryPlan,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        IReadOnlyList<AiTransactionRow> transactions,
        bool sensitiveMode,
        CancellationToken cancellationToken)
    {
        if (!queryPlan.NeedsCategoryLimits || cycles.Count == 0) return null;

        var guideVersions = await _context.CategorySpendingGuides.AsNoTracking().ToListAsync(cancellationToken);
        if (guideVersions.Count == 0) return Array.Empty<object>();

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

        var billStatuses = await LoadRecurringBillStatusesAsync(cycles, cycleDay, cancellationToken);
        var today = _financialClock.Today;
        var results = new List<object>();

        foreach (var cycle in cycles)
        {
            var key = $"{cycle.Year:D4}-{cycle.MonthIndex:D2}";
            var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
            var start = DateOnly.FromDateTime(range.start);
            var end = DateOnly.FromDateTime(range.end);
            var cycleTransactions = transactions.Where(row => IsInCycle(row, cycle, cycleDay)).ToList();
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

            var elapsedDays = today < start ? 0 : today > end
                ? end.DayNumber - start.DayNumber + 1
                : today.DayNumber - start.DayNumber + 1;
            var totalDays = end.DayNumber - start.DayNumber + 1;
            var isComplete = today > end;

            foreach (var guide in effectiveGuides)
            {
                var categoryRows = cycleTransactions
                    .Where(row => row.Amount < 0 && !IsTransfer(row) &&
                        string.Equals(row.Category, guide.CategoryName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var spent = Math.Abs(categoryRows.Sum(row => row.Amount));
                var recurringSpent = Math.Abs(categoryRows
                    .Where(row => !string.IsNullOrWhiteSpace(row.RecurringPaymentId))
                    .Sum(row => row.Amount));
                var pending = billStatuses
                    .Where(item => item.Status == "Pending" &&
                        DateOnly.TryParse(item.DueDate, out var dueDate) && dueDate >= start && dueDate <= end &&
                        string.Equals(item.Category, guide.CategoryName, StringComparison.OrdinalIgnoreCase))
                    .Sum(item => item.Amount);
                var nonRecurringSpent = Math.Max(0, spent - recurringSpent);
                var limit = guide.LimitAmount!.Value;
                var projected = isComplete
                    ? spent
                    : recurringSpent + pending + (elapsedDays > 0 ? nonRecurringSpent / elapsedDays * totalDays : 0m);
                var status = spent > limit ? "Exceeded" : projected > limit ? "Watch" : "OnTrack";

                results.Add(sensitiveMode
                    ? new
                    {
                        cycle = key, category = guide.CategoryName, status, isComplete,
                        observedThrough = today < start ? (string?)null : (today < end ? today : end).ToString("yyyy-MM-dd")
                    }
                    : new
                    {
                        cycle = key, category = guide.CategoryName, limit, spent,
                        remaining = limit - spent, pendingCommitted = pending, projectedSpend = projected,
                        percentUsed = limit > 0 ? (double)(spent / limit) : 0d, status, isComplete,
                        observedThrough = today < start ? (string?)null : (today < end ? today : end).ToString("yyyy-MM-dd")
                    });
            }
        }

        return results;
    }

    private object? BuildCycleInsightsContext(
        AiQueryPlan queryPlan,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        IReadOnlyList<AiTransactionRow> transactions,
        bool sensitiveMode)
    {
        if (!queryPlan.NeedsCycleInsights || cycles.Count == 0) return null;

        var today = _financialClock.Today;
        return cycles.Select(cycle =>
        {
            var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
            var start = DateOnly.FromDateTime(range.start);
            var end = DateOnly.FromDateTime(range.end);
            var totalDays = end.DayNumber - start.DayNumber + 1;
            var elapsedDays = today < start ? 0 : today > end ? totalDays : today.DayNumber - start.DayNumber + 1;
            var isComplete = today > end;
            var expenses = transactions
                .Where(row => IsInCycle(row, cycle, cycleDay) && row.Amount < 0 && !IsTransfer(row))
                .ToList();
            var largest = expenses.OrderBy(row => row.Amount).FirstOrDefault();
            var biggestDay = expenses.GroupBy(row => row.Date)
                .Select(group => new { date = group.Key, total = Math.Abs(group.Sum(row => row.Amount)) })
                .OrderByDescending(day => day.total)
                .FirstOrDefault();
            var endExclusive = range.end.AddDays(1);
            var midpoint = range.start.Ticks + (endExclusive.Ticks - range.start.Ticks) / 2;
            var observedDays = Math.Max(0, elapsedDays);
            var noSpendDays = Math.Max(0, observedDays -
                expenses.Select(row => row.Date).Distinct(StringComparer.Ordinal).Count());
            var committed = expenses
                .Where(row => !string.IsNullOrWhiteSpace(row.RecurringPaymentId))
                .Sum(row => Math.Abs(row.Amount));

            return (object)new
            {
                cycle = $"{cycle.Year:D4}-{cycle.MonthIndex:D2}",
                label = range.label,
                isComplete,
                phase = isComplete ? "Completed" : today < start ? "NotStarted" : "InProgress",
                observedThrough = today < start ? null : (today < end ? today : end).ToString("yyyy-MM-dd"),
                cycleLengthDays = totalDays,
                elapsedDays,
                remainingDays = Math.Max(0, totalDays - elapsedDays),
                transactionCount = expenses.Count,
                noSpendDaysObserved = noSpendDays,
                largestExpense = sensitiveMode || largest == null ? null : new
                {
                    largest.Description, amount = Math.Abs(largest.Amount), largest.Date
                },
                biggestSpendingDay = sensitiveMode || biggestDay == null ? null : biggestDay,
                averageDailySpend = sensitiveMode || observedDays <= 0
                    ? (decimal?)null
                    : Math.Abs(expenses.Sum(row => row.Amount)) / observedDays,
                velocityFirstHalf = sensitiveMode ? (decimal?)null :
                    expenses.Where(row => row.Timestamp.Ticks <= midpoint).Sum(row => Math.Abs(row.Amount)),
                velocitySecondHalf = sensitiveMode ? (decimal?)null :
                    expenses.Where(row => row.Timestamp.Ticks > midpoint).Sum(row => Math.Abs(row.Amount)),
                committedSpend = sensitiveMode ? (decimal?)null : committed,
                discretionarySpend = sensitiveMode ? (decimal?)null :
                    Math.Max(0, expenses.Sum(row => Math.Abs(row.Amount)) - committed)
            };
        }).ToList();
    }

    private static bool WantsPayEarlyContext(string text) =>
        Regex.IsMatch(text, @"\b(?:pay|paid|settle|settled)\b.{0,80}\bearly\b|\bpay ahead\b|\badvance payment\b",
            RegexOptions.IgnoreCase);

    private async Task<object?> BuildRecurringAdvanceContextAsync(
        AiQueryPlan queryPlan,
        IReadOnlyList<AiRecurringRow> recurring,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        if (!queryPlan.NeedsRecurring || !WantsPayEarlyContext(queryPlan.QueryText) || recurring.Count == 0) return null;

        var recurringIds = recurring.Select(row => row.Id).ToList();
        var models = await _context.RecurringPayments.AsNoTracking()
            .Where(payment => recurringIds.Contains(payment.Id))
            .ToListAsync(cancellationToken);
        var settled = await _context.Transactions.AsNoTracking()
            .Where(transaction => transaction.RecurringPaymentId != null && transaction.RecurringOccurrenceDate != null)
            .Select(transaction => new { transaction.RecurringPaymentId, transaction.RecurringOccurrenceDate })
            .ToListAsync(cancellationToken);
        var settledByPayment = settled
            .GroupBy(row => row.RecurringPaymentId!)
            .ToDictionary(group => group.Key, group => group.Select(row => row.RecurringOccurrenceDate!.Value).ToHashSet());
        var today = _financialClock.Today;
        var result = new List<object>();

        foreach (var payment in models)
        {
            // An auto-deducted bill has a real next due date but can never be brought forward, so the
            // occurrence scan is skipped entirely rather than offering a date the server would reject.
            var isAutoDeducted = payment.PaymentMode == Models.RecurringPaymentMode.AutoDeduct;
            DateOnly? next = null;
            if (payment.Active && !isAutoDeducted)
            {
                var paid = settledByPayment.GetValueOrDefault(payment.Id) ?? [];
                var (year, month) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
                for (var offset = 0; offset < 60 && next == null; offset++)
                {
                    var targetMonth = month + offset;
                    var targetYear = year + (targetMonth - 1) / 12;
                    targetMonth = (targetMonth - 1) % 12 + 1;
                    var cycle = CategoryAttributionService.GetCycleRange(targetYear, targetMonth, cycleDay);
                    next = _recurringOccurrenceService.GetOccurrencesInRange(payment, cycle.start, cycle.end, cycleDay)
                        .Select(DateOnly.FromDateTime)
                        .FirstOrDefault(date => date > today && !paid.Contains(date));
                    if (next == default) next = null;
                }
            }
            result.Add(new
            {
                payment.Id,
                payment.Name,
                canPayEarly = payment.Active && !isAutoDeducted && next.HasValue,
                nextUnpaidOccurrence = next?.ToString("yyyy-MM-dd"),
                reason = !payment.Active ? "Inactive" : isAutoDeducted ? "AutoDeducted" :
                    next == null ? "NoUpcomingOccurrence" : "Available"
            });
        }
        return result;
    }

    private static object? BuildRecurringReminderStatusContext(
        AiQueryPlan queryPlan,
        Models.FinancialSetting? setting,
        IReadOnlyList<AiRecurringRow> recurring)
    {
        if (!queryPlan.NeedsRecurring ||
            !Regex.IsMatch(queryPlan.QueryText, @"\b(push|notification|notify|remind|reminder)\b", RegexOptions.IgnoreCase))
            return null;

        return new
        {
            accountRemindersEnabled = setting?.PushRemindersEnabled ?? false,
            payments = recurring.Select(row => new
            {
                row.Id, row.Name,
                enabled = row.PushReminderEnabled,
                effective = (setting?.PushRemindersEnabled ?? false) && row.PushReminderEnabled,
                mode = row.PushReminderMode,
                leadDays = row.PushReminderLeadDays
            }).ToList()
        };
    }
}
