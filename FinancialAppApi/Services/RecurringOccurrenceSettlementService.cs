using System.Security.Cryptography;
using System.Text;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public enum RecurringSettlementStatus
{
    Success,
    NotFound,
    Invalid,
    AutomaticPayment,
    Conflict
}

public sealed record RecurringSettlementResult(
    RecurringSettlementStatus Status,
    RecurringPaymentOccurrence? Occurrence = null,
    Transaction? Transaction = null,
    DateOnly? NextOccurrenceDate = null,
    string? Message = null,
    string? Code = null,
    IReadOnlyList<string>? MissingBuckets = null);

public sealed class RecurringOccurrenceSettlementService
{
    private readonly AppDbContext _context;
    private readonly RecurringOccurrenceLedgerService _occurrences;
    private readonly TransactionPersistenceService _transactions;
    private readonly FinancialClock _clock;

    public RecurringOccurrenceSettlementService(
        AppDbContext context,
        RecurringOccurrenceLedgerService occurrences,
        TransactionPersistenceService transactions,
        FinancialClock clock)
    {
        _context = context;
        _occurrences = occurrences;
        _transactions = transactions;
        _clock = clock;
    }

    public async Task<RecurringSettlementResult> SettleAsync(
        string paymentId,
        DateOnly occurrenceDate,
        string status,
        DateOnly? paidDate,
        string? clientKey,
        CancellationToken cancellationToken = default,
        string? transactionId = null,
        DateTime? postedAt = null,
        string? accountId = null,
        decimal? requestedAmount = null)
    {
        if (status is not (RecurringOccurrenceStatus.Paid or RecurringOccurrenceStatus.Discarded))
        {
            return new RecurringSettlementResult(RecurringSettlementStatus.Invalid, Message: "Status must be Paid or Discarded.");
        }

        var payment = await _context.RecurringPayments.FirstOrDefaultAsync(
            candidate => candidate.Id == paymentId,
            cancellationToken);
        if (payment == null) return new RecurringSettlementResult(RecurringSettlementStatus.NotFound);

        RecurringPaymentOccurrence occurrence;
        try
        {
            occurrence = await _occurrences.EnsureOccurrenceAsync(payment, occurrenceDate, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return new RecurringSettlementResult(RecurringSettlementStatus.Invalid, Message: exception.Message);
        }

        var existingTransactions = await _context.Transactions
            .Where(t => t.RecurringPaymentId == paymentId && t.RecurringOccurrenceDate == occurrenceDate)
            .ToListAsync(cancellationToken);

        // The derived key deliberately mixes in how much is already paid. Keyed on the occurrence,
        // status and amount alone, two part payments of the same size against the same bill hashed to
        // one id, so the second was swallowed as an idempotent replay and the money silently vanished
        // — an easy thing to do, since a bill is usually halved rather than split unevenly. A caller
        // that wants true idempotency across retries passes clientKey (the outbox always does); this
        // fallback only has to stop a double-submit of the identical request.
        var resolvedTransactionId = string.IsNullOrWhiteSpace(transactionId)
            ? BuildTransactionId(clientKey
                ?? $"{paymentId}:{occurrenceDate:yyyy-MM-dd}:{status}:{requestedAmount}:{PaidSoFarKeyPart(existingTransactions)}")
            : transactionId;

        // Idempotency check: if this specific transaction already exists, return success.
        var matchedTx = existingTransactions.FirstOrDefault(t => t.Id == resolvedTransactionId);
        if (matchedTx != null)
        {
            var replayNext = (await _occurrences.GetNextPendingAsync(
                payment, _clock.Today, includeFrom: true, cancellationToken))?.OccurrenceDate;
            return new RecurringSettlementResult(
                RecurringSettlementStatus.Success, occurrence, matchedTx, replayNext);
        }

        var nonDiscardedTxs = existingTransactions
            .Where(t => !RecurringOccurrenceAmounts.IsDiscardedMarker(t))
            .ToList();
        var paidSoFar = RecurringOccurrenceAmounts.PaidSoFar(existingTransactions);
        var scheduledAmount = Math.Abs(occurrence.ScheduledAmount ?? payment.Amount);
        var remainingAmount = Math.Max(0m, scheduledAmount - paidSoFar);

        if (occurrence.Status == RecurringOccurrenceStatus.SettledByLoanPayoff)
        {
            return new RecurringSettlementResult(
                RecurringSettlementStatus.Conflict,
                Message: "This occurrence was already settled by a loan payoff.");
        }

        if (status == RecurringOccurrenceStatus.Discarded)
        {
            if (occurrence.Status == RecurringOccurrenceStatus.Discarded)
            {
                var replayNext = (await _occurrences.GetNextPendingAsync(
                    payment, _clock.Today, includeFrom: true, cancellationToken))?.OccurrenceDate;
                return new RecurringSettlementResult(
                    RecurringSettlementStatus.Success, occurrence, nonDiscardedTxs.FirstOrDefault(), replayNext);
            }
            if (paidSoFar > 0m || occurrence.Status == RecurringOccurrenceStatus.PartiallyPaid)
            {
                return new RecurringSettlementResult(
                    RecurringSettlementStatus.Invalid,
                    Message: "A partially paid bill cannot be discarded. Finish the payment or delete the partial payments first.");
            }
        }
        else if (occurrence.Status == RecurringOccurrenceStatus.Paid && remainingAmount == 0m)
        {
            return new RecurringSettlementResult(
                RecurringSettlementStatus.Conflict,
                Message: "This occurrence has already been fully paid.");
        }

        var scheduledBucket = occurrence.LedgerCategory ?? payment.LedgerCategory;
        if (status == RecurringOccurrenceStatus.Paid)
        {
            var scheduledAccountId = occurrence.AccountId ?? payment.AccountId;
            var requestedAccountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
            if (requestedAccountId is null)
                return new RecurringSettlementResult(
                    RecurringSettlementStatus.Invalid,
                    Message: "Choose the account assigned to this recurring payment.",
                    Code: "ledger_account_required",
                    MissingBuckets: [scheduledBucket]);
            if (!string.Equals(requestedAccountId, scheduledAccountId, StringComparison.Ordinal))
                return new RecurringSettlementResult(
                    RecurringSettlementStatus.Invalid,
                    Message: "The account for this bill changed. Refresh and try again.",
                    Code: "ledger_account_invalid",
                    MissingBuckets: [scheduledBucket]);
            var account = await _context.LedgerAccounts.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == requestedAccountId, cancellationToken);
            if (account is null || account.IsArchived
                || !account.Bucket.Equals(scheduledBucket, StringComparison.OrdinalIgnoreCase))
                return new RecurringSettlementResult(
                    RecurringSettlementStatus.Invalid,
                    Message: "Choose an open account in the recurring payment's bucket.",
                    Code: "ledger_account_invalid",
                    MissingBuckets: [scheduledBucket]);
        }

        var postingDate = paidDate ?? _clock.Today;
        if (postingDate > _clock.Today)
        {
            return new RecurringSettlementResult(
                RecurringSettlementStatus.Invalid,
                Message: "The payment date cannot be in the future.");
        }

        if (status == RecurringOccurrenceStatus.Paid && occurrenceDate > _clock.Today)
        {
            if (payment.PaymentMode == RecurringPaymentMode.AutoDeduct)
            {
                return new RecurringSettlementResult(
                    RecurringSettlementStatus.AutomaticPayment,
                    Message: "This bill is deducted automatically, so it can't be paid ahead of time.");
            }
        }

        decimal amountToPay;
        if (status == RecurringOccurrenceStatus.Discarded)
        {
            amountToPay = 0m;
        }
        else
        {
            if (requestedAmount == null)
            {
                amountToPay = remainingAmount;
            }
            else
            {
                var requested = Math.Round(requestedAmount.Value, 2, MidpointRounding.AwayFromZero);
                if (payment.PaymentMode == RecurringPaymentMode.AutoDeduct && requested != remainingAmount)
                {
                    return new RecurringSettlementResult(
                        RecurringSettlementStatus.Invalid,
                        Message: "Automatic deduction bills must be paid in full.");
                }
                if (requested < 0.01m)
                {
                    return new RecurringSettlementResult(
                        RecurringSettlementStatus.Invalid,
                        Message: "Payment amount must be at least 0.01.");
                }
                if (requested > remainingAmount)
                {
                    return new RecurringSettlementResult(
                        RecurringSettlementStatus.Conflict,
                        Message: $"Payment amount cannot exceed the remaining amount of {remainingAmount:N2}.");
                }
                amountToPay = requested;
            }
        }

        var mutation = await _transactions.CreateTransactionAsync(new TransactionMutationRequest(
            resolvedTransactionId,
            postingDate.ToString("yyyy-MM-dd"),
            postedAt?.ToUniversalTime().ToString("O"),
            occurrence.Name,
            occurrence.Category ?? payment.Category,
            status == RecurringOccurrenceStatus.Discarded ? "Discarded" : occurrence.LedgerCategory ?? payment.LedgerCategory,
            ObfuscationHelper.Obfuscate(status == RecurringOccurrenceStatus.Discarded
                ? 0m
                : -Math.Abs(amountToPay)),
            payment.Id,
            null,
            occurrenceDate.ToString("yyyy-MM-dd"),
            AccountId: status == RecurringOccurrenceStatus.Discarded ? null : accountId), cancellationToken);

        if (mutation.Status is not (TransactionMutationStatus.Created or TransactionMutationStatus.Existing))
        {
            return new RecurringSettlementResult(
                mutation.Status == TransactionMutationStatus.Conflict
                    ? RecurringSettlementStatus.Conflict
                    : RecurringSettlementStatus.Invalid,
                Message: mutation.Message);
        }

        // Recompute occurrence status with the newly added transaction.
        var updatedPaid = paidSoFar + amountToPay;
        if (status == RecurringOccurrenceStatus.Discarded)
        {
            occurrence.Status = RecurringOccurrenceStatus.Discarded;
            occurrence.PaidDate = null;
        }
        else if (updatedPaid >= scheduledAmount)
        {
            occurrence.Status = RecurringOccurrenceStatus.Paid;
            occurrence.PaidDate = postingDate;
        }
        else if (updatedPaid > 0m)
        {
            occurrence.Status = RecurringOccurrenceStatus.PartiallyPaid;
            occurrence.PaidDate = null;
        }
        else
        {
            occurrence.Status = RecurringOccurrenceStatus.Pending;
            occurrence.PaidDate = null;
        }

        await _context.SaveChangesAsync(cancellationToken);

        var refreshed = await _context.RecurringPaymentOccurrences.AsNoTracking().SingleAsync(
            item => item.RecurringPaymentId == paymentId && item.OccurrenceDate == occurrenceDate,
            cancellationToken);
        var next = (await _occurrences.GetNextPendingAsync(
            payment, _clock.Today, includeFrom: true, cancellationToken))?.OccurrenceDate;

        return new RecurringSettlementResult(
            RecurringSettlementStatus.Success, refreshed, mutation.Transaction, next);
    }

    private static string PaidSoFarKeyPart(IEnumerable<Transaction> existingTransactions) =>
        RecurringOccurrenceAmounts.PaidSoFar(existingTransactions).ToString("F2");

    private static string BuildTransactionId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"tx-recurring-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
