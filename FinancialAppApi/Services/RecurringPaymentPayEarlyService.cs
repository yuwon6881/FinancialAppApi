using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace FinancialAppApi.Services;

public enum PayEarlyStatus
{
    Success,
    PaymentNotFound,
    PaymentInactive,
    AutomaticPayment,
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

    // An auto-deducted bill leaves the account on the bank's schedule, so there is nothing to bring
    // forward: settling it here would post a ledger row for money that is still going to be taken on
    // the real due date. The client hides the button, but this is the check that actually decides.
    private const string AutomaticPaymentMessage =
        "This bill is deducted automatically, so it can't be paid ahead of time.";

    private static PayEarlyResult? RejectIfNotUserPaid(RecurringPayment payment)
    {
        if (payment.PaymentMode != RecurringPaymentMode.AutoDeduct) return null;
        return new PayEarlyResult(PayEarlyStatus.AutomaticPayment, Message: AutomaticPaymentMessage);
    }

    private readonly AppDbContext _context;
    private readonly RecurringOccurrenceService _occurrenceService;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly FinancialClock _financialClock;

    public RecurringPaymentPayEarlyService(
        AppDbContext context,
        RecurringOccurrenceService occurrenceService,
        CycleBalanceService cycleBalanceService,
        FinancialClock? financialClock = null)
    {
        _context = context;
        _occurrenceService = occurrenceService;
        _cycleBalanceService = cycleBalanceService;
        _financialClock = financialClock ?? FinancialClock.Utc;
    }

    public async Task<PayEarlyResult> PayEarlyAsync(
        string recurringPaymentId,
        CancellationToken cancellationToken = default,
        string? clientKey = null)
    {
        var candidate = await ResolveNextFutureOccurrenceAsync(recurringPaymentId, cancellationToken);
        if (candidate.Result != null)
        {
            return candidate.Result;
        }

        return await PayEarlyAsync(recurringPaymentId, candidate.Occurrence!.Value, cancellationToken, clientKey);
    }

    public async Task<PayEarlyResult> PayEarlyAsync(
        string recurringPaymentId,
        DateOnly expectedOccurrenceDate,
        CancellationToken cancellationToken = default,
        string? clientKey = null)
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
        var automaticRejection = RejectIfNotUserPaid(payment);
        if (automaticRejection != null)
        {
            return automaticRejection;
        }

        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        var cycleDay = setting?.CycleDay ?? FinancialConstants.DefaultCycleDay;
        var today = _financialClock.Today;

        // The frontend supplies the outbox operation id as a client key. If the response to a
        // successful POST was lost, replaying the queued operation must return the original row
        // rather than attempting to settle a later occurrence. The key is hashed into a bounded,
        // opaque transaction id so arbitrary client input never becomes a database key.
        if (!string.IsNullOrWhiteSpace(clientKey))
        {
            var existing = await _context.Transactions.FirstOrDefaultAsync(
                transaction => transaction.Id == BuildClientTransactionId(clientKey),
                cancellationToken);
            if (existing != null)
            {
                if (existing.RecurringPaymentId != payment.Id || existing.RecurringOccurrenceDate == null)
                {
                    return new PayEarlyResult(PayEarlyStatus.Conflict, Message: "This pay-early request key was already used.");
                }

                var nextAfterReplay = await FindNextUnpaidOccurrenceAsync(
                    payment, cycleDay, today, includeToday: false, cancellationToken);
                return new PayEarlyResult(
                    PayEarlyStatus.Success,
                    existing,
                    existing.RecurringOccurrenceDate.Value,
                    nextAfterReplay);
            }
        }

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
            Id = string.IsNullOrWhiteSpace(clientKey)
                ? $"tx-payearly-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"
                : BuildClientTransactionId(clientKey),
            Date = TransactionDate.FromInputDate(today),
            PostedAt = DateTime.UtcNow,
            Description = payment.Name,
            Category = payment.Category,
            LedgerCategory = payment.LedgerCategory,
            // Recurring templates store a positive cost. Ledger expenses are negative (the
            // normal confirm-payment flow also applies -Abs), so paying early must not credit
            // the selected envelope or disappear from spending/category-watch calculations.
            Amount = -Math.Abs(payment.Amount),
            RecurringPaymentId = payment.Id,
            RecurringOccurrenceDate = occurrence.Value
        };

        _context.Transactions.Add(transaction);

        try
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
                var (affectedYear, affectedMonth) =
                    CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
                await _cycleBalanceService.InvalidateFromAsync(affectedYear, affectedMonth);
                await dbTransaction.CommitAsync(cancellationToken);
            });
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
        var automaticRejection = RejectIfNotUserPaid(payment);
        if (automaticRejection != null)
        {
            // Checked here as well as in the date-taking overload so the caller gets the real reason
            // rather than whatever the occurrence scan happens to conclude.
            return (null, automaticRejection);
        }

        var setting = await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var occurrence = await FindNextUnpaidOccurrenceAsync(
            payment,
            setting?.CycleDay ?? FinancialConstants.DefaultCycleDay,
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
            setting?.CycleDay ?? FinancialConstants.DefaultCycleDay,
            _financialClock.Today,
            includeToday: true,
            cancellationToken);
    }

    /// <summary>
    /// The next unpaid occurrence for every payment, resolved in two queries rather than three per payment.
    /// </summary>
    /// <remarks>
    /// The per-payment overload is the right shape when settling one bill, but the list endpoints
    /// (and the composite boot payload behind them) need this for every row, and each call there was
    /// re-reading the payment it had already been handed plus the one FinancialSettings row that
    /// cannot differ between them. Twenty bills cost sixty-one round trips; they now cost two.
    /// The scan itself is unchanged — it is the same pure walk over the same settled set.
    /// </remarks>
    public async Task<Dictionary<string, DateOnly>> GetNextUnpaidOccurrencesAsync(
        IReadOnlyList<RecurringPaymentProjection> payments,
        CancellationToken cancellationToken = default)
    {
        // The recurrence engine reads only these five fields, and the list endpoints hold a
        // projection rather than the entity — rehydrating one here keeps a single scan
        // implementation instead of a second overload that could drift from it.
        var active = payments
            .Where(p => p.Active)
            .Select(p => new RecurringPayment
            {
                Id = p.Id,
                StartDate = p.StartDate,
                EndDate = p.EndDate,
                Frequency = p.Frequency,
                DueDate = p.DueDate,
                Active = p.Active,
            })
            .ToList();
        if (active.Count == 0) return [];

        var setting = await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var cycleDay = setting?.CycleDay ?? FinancialConstants.DefaultCycleDay;
        var today = _financialClock.Today;

        var ids = active.Select(p => p.Id).ToList();
        var settledRows = await _context.Transactions
            .Where(t => t.RecurringPaymentId != null
                        && ids.Contains(t.RecurringPaymentId)
                        && t.RecurringOccurrenceDate != null)
            .Select(t => new { t.RecurringPaymentId, Date = t.RecurringOccurrenceDate!.Value })
            .ToListAsync(cancellationToken);
        var settledByPayment = settledRows
            .GroupBy(row => row.RecurringPaymentId!)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Date).ToHashSet());

        var result = new Dictionary<string, DateOnly>(active.Count);
        foreach (var payment in active)
        {
            var settled = settledByPayment.GetValueOrDefault(payment.Id) ?? [];
            var occurrence = FindNextUnpaidOccurrence(payment, cycleDay, today, includeToday: true, settled);
            if (occurrence != null) result[payment.Id] = occurrence.Value;
        }
        return result;
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

        return FindNextUnpaidOccurrence(payment, cycleDay, today, includeToday, settledOccurrences.ToHashSet());
    }

    private DateOnly? FindNextUnpaidOccurrence(
        RecurringPayment payment,
        int cycleDay,
        DateOnly today,
        bool includeToday,
        HashSet<DateOnly> settledSet)
    {
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

    private static string BuildClientTransactionId(string clientKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(clientKey));
        return $"tx-payearly-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
