using System.Security.Cryptography;
using System.Text;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Loans;

public partial class LoanRepaymentService
{
    public async Task<LoanRepaymentActionResult> UndoRepaymentActionAsync(
        string actionId,
        CancellationToken cancellationToken = default)
    {
        var loanId = await _context.LoanRepaymentActions
            .Where(a => a.Id == actionId)
            .Select(a => a.LoanId)
            .FirstOrDefaultAsync(cancellationToken);
        if (loanId == null) return new LoanRepaymentActionResult(LoanRepaymentStatus.NotFound);

        var paymentId = await _context.LoanRepaymentActions
            .Where(action => action.Id == actionId)
            .Select(action => action.RecurringPaymentId)
            .FirstAsync(cancellationToken);
        await using var paymentLease = await _paymentLock.AcquireAsync(paymentId, cancellationToken);

        var outcome = await InTransactionAsync(
            () => UndoRepaymentActionCoreAsync(actionId, cancellationToken),
            cancellationToken);
        if (outcome.Status != LoanRepaymentStatus.Success) return outcome;
        return outcome with { LoanView = await _loanService.GetLoanAsync(loanId, cancellationToken) };
    }

    private async Task<LoanRepaymentActionResult> UndoRepaymentActionCoreAsync(
        string actionId,
        CancellationToken cancellationToken)
    {
        var action = await _context.LoanRepaymentActions
            .FirstOrDefaultAsync(a => a.Id == actionId, cancellationToken);
        if (action == null) return new LoanRepaymentActionResult(LoanRepaymentStatus.NotFound);

        var txIds = action.TransactionIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (action.Kind == LoanRepaymentActionKind.FullSettlement)
        {
            var payment = await _context.RecurringPayments.FirstOrDefaultAsync(p => p.Id == action.RecurringPaymentId, cancellationToken);
            if (payment != null)
            {
                payment.Active = action.PriorPaymentActive;
                payment.EndDate = action.PriorPaymentEndDate;
            }

            // Only the occurrences this payoff actually swallowed, and each restored to what its own
            // ledger rows say rather than a blanket Pending: a row that was PartiallyPaid when the
            // payoff took it still has its part-payment, and coming back as Pending would leave the
            // status contradicting the money sitting against it.
            var targetDates = action.TargetOccurrenceDates
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => DateOnly.TryParse(value, out var parsed) ? parsed : (DateOnly?)null)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();

            var occurrences = await _context.RecurringPaymentOccurrences
                .Where(o => o.RecurringPaymentId == action.RecurringPaymentId
                    && o.Status == RecurringOccurrenceStatus.SettledByLoanPayoff
                    && targetDates.Contains(o.OccurrenceDate))
                .ToListAsync(cancellationToken);

            if (occurrences.Count > 0)
            {
                var occurrenceDates = occurrences.Select(o => o.OccurrenceDate).ToList();
                var relatedTransactions = await _context.Transactions
                    .Where(t => t.RecurringPaymentId == action.RecurringPaymentId
                        && t.RecurringOccurrenceDate != null
                        && occurrenceDates.Contains(t.RecurringOccurrenceDate.Value)
                        && !txIds.Contains(t.Id))
                    .ToListAsync(cancellationToken);

                foreach (var occ in occurrences)
                {
                    // RecomputeOccurrenceStatus refuses to touch a SettledByLoanPayoff row, which is
                    // what protects it from ordinary transaction edits; this undo is the one caller
                    // entitled to reopen it, so it clears the marker first.
                    occ.Status = RecurringOccurrenceStatus.Pending;
                    occ.PaidDate = null;
                    RecurringOccurrenceLedgerService.RecomputeOccurrenceStatus(
                        occ,
                        relatedTransactions.Where(t => t.RecurringOccurrenceDate == occ.OccurrenceDate).ToList());
                }
            }
        }

        if (txIds.Length > 0)
        {
            // A rejected delete used to be discarded, so the action row vanished while its
            // transactions stayed on the ledger and could never be undone again.
            var deletion = await _transactions.DeleteTransactionsAsync(
                txIds,
                cancellationToken,
                allowLoanRepaymentActionTransactions: true);
            if (deletion.Status is not (TransactionMutationStatus.Deleted or TransactionMutationStatus.NotFound))
            {
                return new LoanRepaymentActionResult(
                    LoanRepaymentStatus.Conflict,
                    Message: deletion.Message ?? "The repayment transactions could not be removed.");
            }
        }

        _context.LoanRepaymentActions.Remove(action);

        return new LoanRepaymentActionResult(
            LoanRepaymentStatus.Success,
            ActionId: actionId,
            Kind: action.Kind);
    }

    private static string BuildRepaymentTransactionId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"tx-loanrepay-{Convert.ToHexString(hash).ToLowerInvariant()[..16]}";
    }

    private async Task<IAsyncDisposable> AcquireLoanPaymentLockAsync(
        string loanId,
        CancellationToken cancellationToken)
    {
        var paymentId = await _context.Loans.AsNoTracking()
            .Where(loan => loan.Id == loanId)
            .Select(loan => loan.RecurringPaymentId)
            .FirstOrDefaultAsync(cancellationToken);
        return await _paymentLock.AcquireAsync(paymentId ?? string.Empty, cancellationToken);
    }

    private async Task<LoanRepaymentActionResult?> ReplayExistingActionAsync(
        string loanId,
        string? clientKey,
        string expectedKind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientKey)) return null;
        var actionId = BuildRepaymentActionId(clientKey);
        var action = await _context.LoanRepaymentActions.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == actionId, cancellationToken);
        if (action == null) return null;
        if (action.LoanId != loanId || action.Kind != expectedKind)
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Conflict,
                Message: "This repayment request key was already used for another action.");
        }

        var transactionIds = action.TransactionIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var transactions = await _context.Transactions.AsNoTracking()
            .Where(transaction => transactionIds.Contains(transaction.Id))
            .ToListAsync(cancellationToken);
        return new LoanRepaymentActionResult(
            LoanRepaymentStatus.Success,
            ActionId: action.Id,
            Kind: action.Kind,
            Transactions: transactions);
    }

    private static string BuildRepaymentActionId(string? clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey)) return $"repay-{Guid.NewGuid():N}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"loan-repayment-action:{clientKey}"));
        return $"repay-{Convert.ToHexString(hash).ToLowerInvariant()[..24]}";
    }
}
