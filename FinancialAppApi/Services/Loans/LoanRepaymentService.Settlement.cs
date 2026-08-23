using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Loans;

public partial class LoanRepaymentService
{
    public async Task<LoanRepaymentActionResult> FullSettlementRepaymentAsync(
        string loanId,
        decimal lenderQuoteAmount,
        string? accountId,
        string? clientKey,
        DateTime? postedAt = null,
        CancellationToken cancellationToken = default)
    {
        if (lenderQuoteAmount < 0.01m)
        {
            return new LoanRepaymentActionResult(LoanRepaymentStatus.Invalid, Message: "Settlement amount must be at least 0.01.");
        }

        await using var paymentLease = await AcquireLoanPaymentLockAsync(loanId, cancellationToken);

        var outcome = await InTransactionAsync(
            () => FullSettlementCoreAsync(loanId, lenderQuoteAmount, accountId, clientKey, postedAt, cancellationToken),
            cancellationToken);
        if (outcome.Status != LoanRepaymentStatus.Success) return outcome;
        return outcome with { LoanView = await _loanService.GetLoanAsync(loanId, cancellationToken) };
    }

    private async Task<LoanRepaymentActionResult> FullSettlementCoreAsync(
        string loanId,
        decimal lenderQuoteAmount,
        string? accountId,
        string? clientKey,
        DateTime? postedAt,
        CancellationToken cancellationToken)
    {
        var loan = await _context.Loans.FirstOrDefaultAsync(l => l.Id == loanId, cancellationToken);
        if (loan == null) return new LoanRepaymentActionResult(LoanRepaymentStatus.NotFound);

        var payment = await _context.RecurringPayments.FirstOrDefaultAsync(p => p.Id == loan.RecurringPaymentId, cancellationToken);
        if (payment == null) return new LoanRepaymentActionResult(LoanRepaymentStatus.NotFound);

        var replay = await ReplayExistingActionAsync(loanId, clientKey, LoanRepaymentActionKind.FullSettlement, cancellationToken);
        if (replay != null) return replay;

        // The advance path refuses both of these, and a payoff writes far more state, so it must not
        // be the lenient one. An incomplete schedule means the replay cannot say what is really owed.
        if (payment.PaymentMode == RecurringPaymentMode.AutoDeduct)
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Conflict,
                Message: "An automatically deducted bill cannot be settled early. Switch it to manual payment first.");
        }

        // One payoff per loan, asked before the balance is read: a settled loan's replay is overridden
        // to a zero balance, so checking that first answered a second settlement with "already fully
        // paid", which names the symptom rather than the thing standing in the way.
        var alreadySettled = await _context.LoanRepaymentActions
            .AnyAsync(a => a.LoanId == loan.Id && a.Kind == LoanRepaymentActionKind.FullSettlement, cancellationToken);
        if (alreadySettled)
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Conflict,
                Message: "This loan has already been settled. Undo the existing settlement first.");
        }

        var view = await _loanService.GetLoanAsync(loanId, cancellationToken);
        if (view == null || view.Loan.ScheduleStatus == LoanScheduleStatus.Incomplete)
        {
            return new LoanRepaymentActionResult(LoanRepaymentStatus.Invalid, Message: "Loan schedule is incomplete or unavailable.");
        }
        if (view.Replay.OutstandingBalance <= 0m)
        {
            return new LoanRepaymentActionResult(LoanRepaymentStatus.Invalid, Message: "This loan has already been fully paid.");
        }

        // Payoff closes the bill as of today, so anything still open from before today would be
        // stranded: invisible once the bill goes inactive, yet still counted as committed money and
        // still blocking deletion. Those are real unpaid instalments, so the user resolves them.
        var arrears = await _context.RecurringPaymentOccurrences
            .Where(o => o.RecurringPaymentId == payment.Id
                && o.OccurrenceDate < _clock.Today
                && (o.Status == RecurringOccurrenceStatus.Pending || o.Status == RecurringOccurrenceStatus.PartiallyPaid))
            .OrderBy(o => o.OccurrenceDate)
            .Select(o => o.OccurrenceDate)
            .ToListAsync(cancellationToken);
        if (arrears.Count > 0)
        {
            var dates = string.Join(", ", arrears.Select(d => d.ToString("yyyy-MM-dd")));
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Conflict,
                Message: $"Record or discard the payments still open from before today first: {dates}.");
        }

        var requestedAccountId = string.IsNullOrWhiteSpace(accountId) ? payment.AccountId : accountId.Trim();
        if (string.IsNullOrWhiteSpace(requestedAccountId))
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Invalid,
                Message: "Choose the account for this loan full settlement.",
                Code: "ledger_account_required");
        }

        var account = await _context.LedgerAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == requestedAccountId, cancellationToken);
        if (account is null || account.IsArchived)
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Invalid,
                Message: "Choose an open account for this loan settlement.",
                Code: "ledger_account_invalid");
        }

        var nextOccurrence = view.Replay.FutureSchedule.FirstOrDefault()?.OccurrenceDate ?? _clock.Today;
        var dateStr = nextOccurrence.ToString("yyyy-MM-dd");
        var txId = string.IsNullOrWhiteSpace(clientKey)
            ? BuildRepaymentTransactionId($"{loanId}:settlement:{dateStr}")
            : clientKey;

        var mutation = await _transactions.CreateTransactionAsync(new TransactionMutationRequest(
            txId,
            _clock.Today.ToString("yyyy-MM-dd"),
            postedAt?.ToUniversalTime().ToString("O"),
            $"{loan.Name} full settlement",
            payment.Category,
            account.Bucket,
            ObfuscationHelper.Obfuscate(-Math.Abs(lenderQuoteAmount)),
            payment.Id,
            null,
            dateStr,
            AccountId: requestedAccountId), cancellationToken);

        if (mutation.Status is not (TransactionMutationStatus.Created or TransactionMutationStatus.Existing))
        {
            return new LoanRepaymentActionResult(
                mutation.Status == TransactionMutationStatus.Conflict ? LoanRepaymentStatus.Conflict : LoanRepaymentStatus.Invalid,
                Message: mutation.Message ?? "Failed to create settlement transaction.");
        }

        var priorActive = payment.Active;
        var priorEndDate = payment.EndDate;

        // Stop the linked recurring bill as of today
        payment.Active = false;
        payment.EndDate = _clock.Today.ToString("yyyy-MM-dd");

        // Every still-open occurrence, with no date floor. The arrears guard above has already
        // established there is nothing before today, so this is the same set — but written without a
        // floor it cannot silently strand a row if that guard is ever relaxed.
        var occurrencesToResolve = await _context.RecurringPaymentOccurrences
            .Where(o => o.RecurringPaymentId == payment.Id
                && (o.Status == RecurringOccurrenceStatus.Pending
                    || o.Status == RecurringOccurrenceStatus.PartiallyPaid
                    || o.OccurrenceDate == nextOccurrence))
            .ToListAsync(cancellationToken);

        var settledDates = new List<string>(occurrencesToResolve.Count);
        foreach (var occ in occurrencesToResolve)
        {
            occ.Status = RecurringOccurrenceStatus.SettledByLoanPayoff;
            settledDates.Add(occ.OccurrenceDate.ToString("yyyy-MM-dd"));
        }

        var actionId = BuildRepaymentActionId(clientKey);
        var createdTx = mutation.Transaction!;
        var action = new LoanRepaymentAction
        {
            Id = actionId,
            UserId = loan.UserId,
            LoanId = loan.Id,
            RecurringPaymentId = loan.RecurringPaymentId,
            Kind = LoanRepaymentActionKind.FullSettlement,
            LenderQuoteAmount = lenderQuoteAmount,
            EffectiveDate = _clock.Today,
            ReplayRevision = Guid.NewGuid().ToString("N"),
            PriorPaymentActive = priorActive,
            PriorPaymentEndDate = priorEndDate,
            TransactionIds = createdTx.Id,
            // Every occurrence this payoff swallowed, so undo restores exactly those and leaves a row
            // settled by some other action alone.
            TargetOccurrenceDates = string.Join(",", settledDates),
            UndoFingerprints = JsonSerializer.Serialize(new[] { new { createdTx.Id, createdTx.Amount, createdTx.Date } }),
            CreatedAt = DateTime.UtcNow
        };

        _context.LoanRepaymentActions.Add(action);

        return new LoanRepaymentActionResult(
            LoanRepaymentStatus.Success,
            ActionId: actionId,
            Kind: LoanRepaymentActionKind.FullSettlement,
            Transactions: [createdTx]);
    }
}
