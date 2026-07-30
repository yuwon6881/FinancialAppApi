using FinancialAppApi.Database;
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
        var cycleDay = setting.CycleDay;

        // AsNoTracking: this method only reads. It runs on the dashboard request, so the
        // change-tracker snapshot it used to take for every active payment was pure overhead.
        var activeRecurring = await _context.RecurringPayments
            .AsNoTracking()
            .Where(r => r.Active)
            .ToListAsync(cancellationToken);
        // Confirmed bills are persisted with a freshly generated transaction id, so paid-detection
        // has to go through the RecurringPaymentId link rather than an id match.
        var recurringTransactions = await _context.Transactions
            .Where(t => t.RecurringPaymentId != null)
            .Select(t => new { t.RecurringPaymentId, t.Date, t.RecurringOccurrenceDate })
            .ToListAsync(cancellationToken);
        var today = _financialClock.LocalNow.Date;
        var (todayMonth, todayYear) = CategoryAttributionService.GetCycleMonthAndYearForDate(today, cycleDay);
        var todayMonthIdx = Array.IndexOf(Months, todayMonth) + 1;

        var pending = new List<object>();

        foreach (var rp in activeRecurring)
        {
            if (!DateTime.TryParse(rp.StartDate, out var startDate)) continue;

            int startYear = Math.Min(2026, startDate.Year);
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
                        var isPaid = recurringTransactions.Any(t =>
                            t.RecurringPaymentId == rp.Id &&
                            (t.RecurringOccurrenceDate == billingDateOnly ||
                             (t.RecurringOccurrenceDate == null &&
                              t.Date >= cycleStartDate &&
                              t.Date < cycleEndExclusive)));
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
}
