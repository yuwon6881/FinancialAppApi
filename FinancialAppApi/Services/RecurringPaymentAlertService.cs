using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public class RecurringPaymentAlertService
{
    private static readonly string[] Months = FinancialConstants.MonthAbbreviations;

    private readonly AppDbContext _context;
    private readonly FinancialClock _financialClock;
    private readonly RecurringOccurrenceLedgerService _occurrenceLedger;

    public RecurringPaymentAlertService(
        AppDbContext context,
        RecurringOccurrenceService occurrenceService,
        FinancialClock? financialClock = null,
        RecurringOccurrenceLedgerService? occurrenceLedger = null)
    {
        _context = context;
        _financialClock = financialClock ?? FinancialClock.Utc;
        _occurrenceLedger = occurrenceLedger ?? new RecurringOccurrenceLedgerService(context, occurrenceService, _financialClock);
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
        var occurrences = await _occurrenceLedger.GetPendingDueAsync(activeRecurring, cancellationToken);
        if (occurrences.Count == 0) return [];

        var partiallyPaid = occurrences.Where(o => o.Status == RecurringOccurrenceStatus.PartiallyPaid).ToList();
        var transactionsByOcc = new Dictionary<(string PaymentId, DateOnly Date), List<Transaction>>();
        if (partiallyPaid.Count > 0)
        {
            var occurrenceDates = partiallyPaid.Select(o => o.OccurrenceDate).Distinct().ToList();
            var paymentIds = partiallyPaid.Select(o => o.RecurringPaymentId).Distinct().ToList();
            var transactions = await _context.Transactions
                .AsNoTracking()
                .Where(t => t.RecurringPaymentId != null
                    && paymentIds.Contains(t.RecurringPaymentId)
                    && t.RecurringOccurrenceDate != null
                    && occurrenceDates.Contains(t.RecurringOccurrenceDate.Value))
                .ToListAsync(cancellationToken);

            transactionsByOcc = transactions
                .GroupBy(t => (PaymentId: t.RecurringPaymentId!, Date: t.RecurringOccurrenceDate!.Value))
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        return occurrences.Select(occurrence =>
        {
            var txs = transactionsByOcc.GetValueOrDefault((occurrence.RecurringPaymentId, occurrence.OccurrenceDate)) ?? [];
            var nonDiscarded = txs.Where(t => !string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase)).ToList();
            var paidSoFar = nonDiscarded.Sum(t => Math.Abs(t.Amount));
            var scheduled = occurrence.ScheduledAmount ?? 0m;
            var remaining = Math.Max(0m, scheduled - paidSoFar);

            var (month, year) = CategoryAttributionService.GetCycleMonthAndYearForDate(
                occurrence.OccurrenceDate.ToDateTime(TimeOnly.MinValue), cycleDay);
            var monthIndex = Array.IndexOf(Months, month) + 1;
            var (_, _, cycleLabel) = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
            return (object)new
            {
                id = occurrence.Id,
                recurringPaymentId = occurrence.RecurringPaymentId,
                name = occurrence.Name,
                amount = ObfuscationHelper.Obfuscate(occurrence.Status == RecurringOccurrenceStatus.PartiallyPaid ? remaining : scheduled),
                scheduledAmount = ObfuscationHelper.Obfuscate(scheduled),
                paidAmount = ObfuscationHelper.Obfuscate(paidSoFar),
                remainingAmount = ObfuscationHelper.Obfuscate(remaining),
                status = occurrence.Status,
                category = occurrence.Category ?? string.Empty,
                ledgerCategory = occurrence.LedgerCategory ?? string.Empty,
                // The occurrence's frozen account, so confirming settles where this bill was
                // scheduled rather than wherever the schedule points now. Null on legacy rows; the
                // client falls back to the parent exactly as the server does.
                accountId = occurrence.AccountId,
                billingDate = occurrence.OccurrenceDate.ToString("yyyy-MM-dd"),
                year,
                month = monthIndex,
                cycleLabel
            };
        }).ToList();
    }

}
