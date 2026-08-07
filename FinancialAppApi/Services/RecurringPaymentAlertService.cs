using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public class RecurringPaymentAlertService
{
    private static readonly string[] Months = FinancialConstants.MonthAbbreviations;

    private readonly AppDbContext _context;
    private readonly RecurringOccurrenceService _occurrenceService;
    private readonly FinancialClock _financialClock;

    public RecurringPaymentAlertService(
        AppDbContext context,
        RecurringOccurrenceService occurrenceService,
        FinancialClock? financialClock = null)
    {
        _context = context;
        _occurrenceService = occurrenceService;
        _financialClock = financialClock ?? FinancialClock.Utc;
    }

    public async Task<List<object>> GetSubscriptionAlertsAsync(
        CancellationToken cancellationToken = default)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null) return new List<object>();

        // AsNoTracking: this method only reads. It runs on the dashboard request, so the
        // change-tracker snapshot it used to take for every active payment was pure overhead.
        var activeRecurring = await _context.RecurringPayments
            .AsNoTracking()
            .Where(r => r.Active)
            .ToListAsync(cancellationToken);

        return await BuildAlertsAsync(setting.CycleDay, activeRecurring, cancellationToken);
    }

    // Overload for callers that already hold the cycle day and the active payments -- the dashboard
    // path loads both into its bootstrap snapshot, and re-reading them here cost /api/bootstrap two
    // round trips on every cold start and every post-drain reconciliation.
    public Task<List<object>> GetSubscriptionAlertsAsync(
        int cycleDay,
        List<RecurringPayment> activeRecurringPayments,
        CancellationToken cancellationToken = default) =>
        BuildAlertsAsync(cycleDay, activeRecurringPayments, cancellationToken);

    private async Task<List<object>> BuildAlertsAsync(
        int cycleDay,
        List<RecurringPayment> activeRecurring,
        CancellationToken cancellationToken)
    {
        // Confirmed bills are persisted with a freshly generated transaction id, so paid-detection
        // has to go through the RecurringPaymentId link rather than an id match.
        //
        // The scan below never looks earlier than the earliest active payment's start year, so
        // bounding the query there is free. It is deliberately NOT bounded to a few recent cycles:
        // an occurrence stays pending precisely because nobody has said what happened to it, and
        // discarding one writes a Discarded transaction. Dropping old ones would not hide noise, it
        // would silently abandon bills that were never recorded either way.
        var earliestScannedCycleStart = EarliestScannedCycleStart(activeRecurring, cycleDay);
        if (earliestScannedCycleStart == null) return new List<object>();

        var historyFrom = TransactionDate.StartOfDate(DateOnly.FromDateTime(earliestScannedCycleStart.Value));
        var recurringTransactions = await _context.Transactions
            .Where(t => t.RecurringPaymentId != null && t.Date >= historyFrom)
            .Select(t => new { t.RecurringPaymentId, t.Date, t.RecurringOccurrenceDate })
            .ToListAsync(cancellationToken);

        // Two lookups, because the paid-check has two arms. A row that names the occurrence it
        // settles matches on identity; a row that does not is matched by posting date falling in
        // the cycle, which still needs a range test -- but only against that one payment's
        // undated rows instead of the whole history, which is what the per-occurrence .Any() scan
        // over every recurring transaction used to do.
        var settledOccurrences = new HashSet<(string, DateOnly)>();
        var undatedByPayment = new Dictionary<string, List<DateTime>>();
        foreach (var transaction in recurringTransactions)
        {
            var paymentId = transaction.RecurringPaymentId!;
            if (transaction.RecurringOccurrenceDate is { } occurrenceDate)
            {
                settledOccurrences.Add((paymentId, occurrenceDate));
                continue;
            }

            if (!undatedByPayment.TryGetValue(paymentId, out var dates))
            {
                dates = new List<DateTime>();
                undatedByPayment[paymentId] = dates;
            }
            dates.Add(transaction.Date);
        }

        var today = _financialClock.LocalNow.Date;
        var (todayMonth, todayYear) = CategoryAttributionService.GetCycleMonthAndYearForDate(today, cycleDay);
        var todayMonthIdx = Array.IndexOf(Months, todayMonth) + 1;

        var pending = new List<object>();

        foreach (var rp in activeRecurring)
        {
            if (!DateTime.TryParse(rp.StartDate, out var startDate)) continue;

            int startYear = Math.Max(2026, startDate.Year);
            for (int y = startYear; y <= todayYear; y++)
            {
                int endMonthIdx = y == todayYear ? todayMonthIdx : 12;
                for (int m = 1; m <= endMonthIdx; m++)
                {
                    var (cycleStart, cycleEnd, cycleLabel) = CategoryAttributionService.GetCycleRange(y, m, cycleDay);
                    foreach (var billingDate in _occurrenceService.GetOccurrencesInRange(rp, cycleStart, cycleEnd, cycleDay))
                    {
                        if (billingDate > today) continue;
                        var instanceId = $"{rp.Id}-{y}-{m}";
                        var billingDateOnly = DateOnly.FromDateTime(billingDate);
                        var cycleStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(cycleStart));
                        var cycleEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(cycleEnd));
                        var isPaid = settledOccurrences.Contains((rp.Id, billingDateOnly)) ||
                            (undatedByPayment.TryGetValue(rp.Id, out var undatedDates) &&
                             undatedDates.Any(date =>
                                 date >= cycleStartDate &&
                                 date < cycleEndExclusive));
                        if (!isPaid)
                        {
                            var item = new
                            {
                                id = instanceId,
                                recurringPaymentId = rp.Id,
                                name = rp.Name,
                                amount = ObfuscationHelper.Obfuscate(rp.Amount),
                                category = rp.Category,
                                ledgerCategory = rp.LedgerCategory,
                                billingDate = billingDate.ToString("yyyy-MM-dd"),
                                year = y,
                                month = m,
                                cycleLabel = cycleLabel
                            };

                            pending.Add(item);
                        }
                    }
                }
            }
        }

        return pending;
    }

    // The earliest cycle start the scan below can reach: it walks each payment from
    // max(2026, its start year) month 1, so nothing before the earliest of those cycle starts is
    // ever consulted. Returns null when no active payment has a parseable start date, which is the
    // same set the scan skips. Resolved through GetCycleRange rather than a Jan 1 boundary because
    // any cycleDay above 1 opens January's cycle in the previous December.
    private static DateTime? EarliestScannedCycleStart(
        List<RecurringPayment> activeRecurring,
        int cycleDay)
    {
        int? earliestYear = null;
        foreach (var payment in activeRecurring)
        {
            if (!DateTime.TryParse(payment.StartDate, out var startDate)) continue;

            var scanYear = Math.Max(2026, startDate.Year);
            if (earliestYear == null || scanYear < earliestYear) earliestYear = scanYear;
        }

        if (earliestYear == null) return null;

        return CategoryAttributionService.GetCycleRange(earliestYear.Value, 1, cycleDay).start;
    }
}
