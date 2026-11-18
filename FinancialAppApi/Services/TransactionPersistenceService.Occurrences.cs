using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class TransactionPersistenceService
{
    // Tags a new transaction with the exact recurrence-engine billing date it settles, when it
    // was created against a recurring payment and its date lines up with that payment's cycle
    // occurrence. Legacy/manual transactions (no match, or no RecurringPaymentId) keep this null.
    private async Task<(bool IsValid, DateOnly? Date, string? Message)> ResolveRecurringOccurrenceDateAsync(
        Transaction transaction,
        string? requestedOccurrenceDate,
        bool authorizedByLoan,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transaction.RecurringPaymentId))
        {
            return string.IsNullOrWhiteSpace(requestedOccurrenceDate)
                ? (true, null, null)
                : (false, null, "A recurring occurrence requires a recurring payment.");
        }

        var payment = await _context.RecurringPayments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == transaction.RecurringPaymentId, cancellationToken);
        if (payment == null)
        {
            return string.IsNullOrWhiteSpace(requestedOccurrenceDate)
                ? (true, null, null)
                : (false, null, "The recurring payment could not be found.");
        }

        var setting = await _context.FinancialSettings.AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        var cycleDay = setting?.CycleDay ?? FinancialConstants.DefaultCycleDay;

        var transactionDate = TransactionDate.ToDateOnly(transaction.Date);
        DateOnly candidateDate;
        if (!string.IsNullOrWhiteSpace(requestedOccurrenceDate))
        {
            if (!DateOnly.TryParseExact(requestedOccurrenceDate, "yyyy-MM-dd", out candidateDate))
            {
                return (false, null, "recurringOccurrenceDate must use yyyy-MM-dd format.");
            }
        }
        else
        {
            candidateDate = transactionDate;
        }

        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(candidateDate, cycleDay);
        var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);

        // A loan instalment date comes from the loan's immutable cadence snapshot, so it stays valid
        // after the bill's due day, frequency, or tracking window is edited.
        DateOnly? matchedOccurrence = authorizedByLoan && !string.IsNullOrWhiteSpace(requestedOccurrenceDate)
            ? candidateDate
            : null;
        if (matchedOccurrence == null)
        foreach (var billingDate in _occurrenceService.GetOccurrencesInRange(payment, cycleStart, cycleEnd, cycleDay))
        {
            if (DateOnly.FromDateTime(billingDate) == candidateDate)
            {
                matchedOccurrence = candidateDate;
                break;
            }
        }
        if (matchedOccurrence == null)
        {
            return string.IsNullOrWhiteSpace(requestedOccurrenceDate)
                ? (true, null, null)
                : (false, null, "The selected date is not an occurrence of this recurring payment.");
        }

        var occurrenceRow = await _context.RecurringPaymentOccurrences.AsNoTracking().FirstOrDefaultAsync(o =>
            o.RecurringPaymentId == payment.Id && o.OccurrenceDate == matchedOccurrence,
            cancellationToken);
        if (occurrenceRow != null && occurrenceRow.Status is RecurringOccurrenceStatus.Discarded or RecurringOccurrenceStatus.SettledByLoanPayoff)
        {
            return (false, null, "This recurring occurrence has already been settled.");
        }
        var existingTxs = await _context.Transactions.AsNoTracking().Where(t =>
            t.RecurringPaymentId == payment.Id && t.RecurringOccurrenceDate == matchedOccurrence
            && t.LedgerCategory.ToUpper() != "DISCARDED")
            .ToListAsync(cancellationToken);
        var paidSoFar = existingTxs.Sum(t => Math.Abs(t.Amount));
        var scheduled = Math.Abs(occurrenceRow?.ScheduledAmount ?? payment.Amount);
        if (scheduled > 0m && paidSoFar >= scheduled)
        {
            return (false, null, "This recurring occurrence has already been settled.");
        }

        return (true, matchedOccurrence, null);
    }

    // Creating a bill payment is capped at what the occurrence still owes; editing one must hold to
    // the same cap, counting every other payment on the occurrence. Loan-linked bills are exempt on
    // both paths because a lender's figure legitimately differs from the scheduled instalment.
    private async Task<TransactionMutationResult?> RejectOccurrenceOverpaymentAsync(
        Transaction transaction,
        string nextLedgerCategory,
        decimal nextAmount,
        CancellationToken cancellationToken)
    {
        if (transaction.RecurringPaymentId is not { } paymentId
            || transaction.RecurringOccurrenceDate is not { } occurrenceDate
            || nextLedgerCategory == RecurringOccurrenceAmounts.DiscardedLedgerCategory
            || Math.Abs(nextAmount) <= Math.Abs(transaction.Amount))
        {
            return null;
        }

        if (await _context.Loans.AsNoTracking().AnyAsync(l => l.RecurringPaymentId == paymentId, cancellationToken))
        {
            return null;
        }

        var payment = await _context.RecurringPayments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        var occurrenceRow = await _context.RecurringPaymentOccurrences.AsNoTracking()
            .FirstOrDefaultAsync(o => o.RecurringPaymentId == paymentId && o.OccurrenceDate == occurrenceDate, cancellationToken);
        var scheduled = Math.Abs(occurrenceRow?.ScheduledAmount ?? payment?.Amount ?? 0m);
        if (scheduled <= 0m) return null;

        var others = await _context.Transactions.AsNoTracking()
            .Where(t => t.RecurringPaymentId == paymentId && t.RecurringOccurrenceDate == occurrenceDate && t.Id != transaction.Id)
            .ToListAsync(cancellationToken);
        var remaining = RecurringOccurrenceAmounts.Remaining(scheduled, RecurringOccurrenceAmounts.PaidSoFar(others));
        if (Math.Abs(nextAmount) <= remaining) return null;

        return new TransactionMutationResult(
            TransactionMutationStatus.Conflict,
            transaction,
            $"Payment amount cannot exceed the remaining amount of {remaining:N2}.");
    }

    private async Task InvalidateCycleBalancesFromAsync(
        DateTime date,
        CancellationToken cancellationToken)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null) return;

        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(TransactionDate.ToDateOnly(date), setting.CycleDay);
        await _cycleBalanceService.InvalidateFromAsync(year, monthIndex, cancellationToken);
    }

    private async Task SaveAndInvalidateCycleBalancesAsync(
        DateTime earliestAffectedDate,
        CancellationToken cancellationToken)
    {
        // A caller that already owns a transaction (loan repayment stages several transactions plus
        // the schedule changes as one unit) commits for us: opening a second execution strategy
        // inside its transaction throws, and committing here would break the atomicity it opened
        // that transaction for. Invalidation still runs, so it is covered by that same commit.
        if (_context.Database.CurrentTransaction is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _context.SaveChangesAsync(cancellationToken);
            await InvalidateCycleBalancesFromAsync(earliestAffectedDate, cancellationToken);
            return;
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await InvalidateCycleBalancesFromAsync(earliestAffectedDate, cancellationToken);
            await dbTransaction.CommitAsync(cancellationToken);
        });
    }
}
