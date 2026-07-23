using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public enum PayEarlyStatus
{
    Success,
    PaymentNotFound,
    PaymentInactive,
    NoUpcomingOccurrence,
    Conflict
}

public sealed record PayEarlyResult(
    PayEarlyStatus Status,
    Transaction? Transaction = null,
    DateOnly? SettledOccurrenceDate = null,
    DateOnly? NextOccurrenceDate = null,
    string? Message = null);

// Lets a user settle the next future, unpaid occurrence of a recurring payment ahead of its
// due date. The occurrence is always derived server-side from the same recurrence engine used
// everywhere else (RecurringOccurrenceService + CategoryAttributionService cycle math); the
// client never gets to name a date, so it cannot be tricked into double-paying or skipping.
public class RecurringPaymentPayEarlyService
{
    // 5 years of monthly cycles is far more runway than any real recurring payment needs; it
    // just bounds the scan so a misconfigured/expired payment can't loop forever.
    private const int MaxCyclesToScan = 60;

    private readonly AppDbContext _context;
    private readonly RecurringOccurrenceService _occurrenceService;
    private readonly FinancialClock _financialClock;

    public RecurringPaymentPayEarlyService(
        AppDbContext context,
        RecurringOccurrenceService occurrenceService,
        FinancialClock? financialClock = null)
    {
        _context = context;
        _occurrenceService = occurrenceService;
        _financialClock = financialClock ?? FinancialClock.Utc;
    }

    public async Task<PayEarlyResult> PayEarlyAsync(string recurringPaymentId, CancellationToken cancellationToken = default)
    {
        var candidate = await ResolveNextFutureOccurrenceAsync(recurringPaymentId, cancellationToken);
        if (candidate.Result != null)
        {
            return candidate.Result;
        }

        return await PayEarlyAsync(recurringPaymentId, candidate.Occurrence!.Value, cancellationToken);
    }

    public async Task<PayEarlyResult> PayEarlyAsync(
        string recurringPaymentId,
        DateOnly expectedOccurrenceDate,
        CancellationToken cancellationToken = default)
    {
        var payment = await _context.RecurringPayments
            .FirstOrDefaultAsync(p => p.Id == recurringPaymentId, cancellationToken);
        if (payment == null)
        {
            return new PayEarlyResult(PayEarlyStatus.PaymentNotFound);
        }
        if (!payment.Active)
        {
            return new PayEarlyResult(PayEarlyStatus.PaymentInactive, Message: "This recurring payment is not active.");
        }

        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        var cycleDay = setting?.CycleDay ?? 1;
        var today = _financialClock.Today;

        var occurrence = await FindNextUnpaidOccurrenceAsync(payment, cycleDay, today, includeToday: false, cancellationToken);
        if (occurrence == null)
        {
            return new PayEarlyResult(
                PayEarlyStatus.NoUpcomingOccurrence,
                Message: "No upcoming occurrence was found for this recurring payment.");
        }
        if (occurrence.Value != expectedOccurrenceDate)
        {
            return new PayEarlyResult(
                PayEarlyStatus.Conflict,
                Message: "The selected occurrence is no longer the next unpaid occurrence. Refresh and try again.");
        }

        // First line of defence against a race: re-check right before the insert. The
        // PostgreSQL partial unique index on (UserId, RecurringPaymentId, RecurringOccurrenceDate)
        // is the actual concurrency-safe guard — this check just turns the common case into a
        // friendly response instead of always relying on catching the constraint violation.
        var alreadySettled = await _context.Transactions.AnyAsync(
            t => t.RecurringPaymentId == payment.Id && t.RecurringOccurrenceDate == occurrence.Value,
            cancellationToken);
        if (alreadySettled)
        {
            return new PayEarlyResult(PayEarlyStatus.Conflict, Message: "This occurrence has already been paid.");
        }

        var transaction = new Transaction
        {
            Id = $"tx-payearly-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}",
            Date = TransactionDate.FromInputDate(today),
            PostedAt = DateTime.UtcNow,
            Description = payment.Name,
            Category = payment.Category,
            LedgerCategory = payment.LedgerCategory,
            Amount = payment.Amount,
            RecurringPaymentId = payment.Id,
            RecurringOccurrenceDate = occurrence.Value
        };

        _context.Transactions.Add(transaction);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // A genuinely concurrent pay-early request committed the same occurrence first.
            return new PayEarlyResult(PayEarlyStatus.Conflict, Message: "This occurrence has already been paid.");
        }

        var nextOccurrence = await FindNextUnpaidOccurrenceAsync(payment, cycleDay, today, includeToday: false, cancellationToken);
        return new PayEarlyResult(PayEarlyStatus.Success, transaction, occurrence.Value, nextOccurrence);
    }

    private async Task<(DateOnly? Occurrence, PayEarlyResult? Result)> ResolveNextFutureOccurrenceAsync(
        string recurringPaymentId,
        CancellationToken cancellationToken)
    {
        var payment = await _context.RecurringPayments
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == recurringPaymentId, cancellationToken);
        if (payment == null)
        {
            return (null, new PayEarlyResult(PayEarlyStatus.PaymentNotFound));
        }
        if (!payment.Active)
        {
            return (null, new PayEarlyResult(PayEarlyStatus.PaymentInactive, Message: "This recurring payment is not active."));
        }

        var setting = await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var occurrence = await FindNextUnpaidOccurrenceAsync(
            payment,
            setting?.CycleDay ?? 1,
            _financialClock.Today,
            includeToday: false,
            cancellationToken);
        return occurrence == null
            ? (null, new PayEarlyResult(PayEarlyStatus.NoUpcomingOccurrence, Message: "No upcoming occurrence was found for this recurring payment."))
            : (occurrence, null);
    }

    public async Task<DateOnly?> GetNextUnpaidOccurrenceAsync(
        string recurringPaymentId,
        CancellationToken cancellationToken = default)
    {
        var payment = await _context.RecurringPayments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == recurringPaymentId, cancellationToken);
        if (payment == null || !payment.Active) return null;
        var setting = await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return await FindNextUnpaidOccurrenceAsync(
            payment,
            setting?.CycleDay ?? 1,
            _financialClock.Today,
            includeToday: true,
            cancellationToken);
    }

    private async Task<DateOnly?> FindNextUnpaidOccurrenceAsync(
        RecurringPayment payment,
        int cycleDay,
        DateOnly today,
        bool includeToday,
        CancellationToken cancellationToken)
    {
        var settledOccurrences = await _context.Transactions
            .Where(t => t.RecurringPaymentId == payment.Id && t.RecurringOccurrenceDate != null)
            .Select(t => t.RecurringOccurrenceDate!.Value)
            .ToListAsync(cancellationToken);
        var settledSet = settledOccurrences.ToHashSet();

        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);

        for (var i = 0; i < MaxCyclesToScan; i++)
        {
            var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
            foreach (var billingDate in _occurrenceService.GetOccurrencesInRange(payment, cycleStart, cycleEnd, cycleDay))
            {
                var occurrenceDate = DateOnly.FromDateTime(billingDate);
                if ((includeToday ? occurrenceDate >= today : occurrenceDate > today) && !settledSet.Contains(occurrenceDate))
                {
                    return occurrenceDate;
                }
            }

            monthIndex++;
            if (monthIndex > 12)
            {
                monthIndex = 1;
                year++;
            }
        }

        return null;
    }
}
