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
    string? Message = null);

public sealed class RecurringOccurrenceSettlementService
{
    private readonly AppDbContext _context;
    private readonly RecurringOccurrenceLedgerService _occurrences;
    private readonly TransactionPersistenceService _transactions;
    private readonly RecurringPaymentPayEarlyService _payEarly;
    private readonly FinancialClock _clock;

    public RecurringOccurrenceSettlementService(
        AppDbContext context,
        RecurringOccurrenceLedgerService occurrences,
        TransactionPersistenceService transactions,
        RecurringPaymentPayEarlyService payEarly,
        FinancialClock clock)
    {
        _context = context;
        _occurrences = occurrences;
        _transactions = transactions;
        _payEarly = payEarly;
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
        DateTime? postedAt = null)
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

        if (occurrence.Status != RecurringOccurrenceStatus.Pending)
        {
            var existing = occurrence.SettlementTransactionId == null
                ? null
                : await _context.Transactions.AsNoTracking().FirstOrDefaultAsync(
                    transaction => transaction.Id == occurrence.SettlementTransactionId,
                    cancellationToken);
            if (occurrence.Status == status)
            {
                var replayNext = (await _occurrences.GetNextPendingAsync(
                    payment, _clock.Today, includeFrom: true, cancellationToken))?.OccurrenceDate;
                return new RecurringSettlementResult(
                    RecurringSettlementStatus.Success, occurrence, existing, replayNext);
            }
            return new RecurringSettlementResult(RecurringSettlementStatus.Conflict, Message: "This occurrence was already reviewed.");
        }

        // Ensure the natural occurrence key exists before the compatibility transaction path
        // resolves it again. The status change and Ledger insert still commit together below.
        await _context.SaveChangesAsync(cancellationToken);

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
            var early = await _payEarly.PayEarlyAsync(
                paymentId, occurrenceDate, cancellationToken, clientKey, transactionId, postedAt);
            return early.Status == PayEarlyStatus.Success
                ? new RecurringSettlementResult(
                    RecurringSettlementStatus.Success,
                    await _context.RecurringPaymentOccurrences.AsNoTracking().SingleAsync(
                        item => item.RecurringPaymentId == paymentId && item.OccurrenceDate == occurrenceDate,
                        cancellationToken),
                    early.Transaction,
                    early.NextOccurrenceDate)
                : new RecurringSettlementResult(
                    early.Status == PayEarlyStatus.Conflict ? RecurringSettlementStatus.Conflict : RecurringSettlementStatus.Invalid,
                    Message: early.Message);
        }

        var resolvedTransactionId = string.IsNullOrWhiteSpace(transactionId)
            ? BuildTransactionId(clientKey ?? $"{paymentId}:{occurrenceDate:yyyy-MM-dd}:{status}")
            : transactionId;
        var mutation = await _transactions.CreateTransactionAsync(new TransactionMutationRequest(
            resolvedTransactionId,
            postingDate.ToString("yyyy-MM-dd"),
            postedAt?.ToUniversalTime().ToString("O"),
            occurrence.Name,
            occurrence.Category ?? payment.Category,
            status == RecurringOccurrenceStatus.Discarded ? "Discarded" : occurrence.LedgerCategory ?? payment.LedgerCategory,
            ObfuscationHelper.Obfuscate(status == RecurringOccurrenceStatus.Discarded
                ? 0m
                : -Math.Abs(occurrence.ScheduledAmount ?? payment.Amount)),
            payment.Id,
            null,
            occurrenceDate.ToString("yyyy-MM-dd")), cancellationToken);

        if (mutation.Status is not (TransactionMutationStatus.Created or TransactionMutationStatus.Existing))
        {
            return new RecurringSettlementResult(
                mutation.Status == TransactionMutationStatus.Conflict
                    ? RecurringSettlementStatus.Conflict
                    : RecurringSettlementStatus.Invalid,
                Message: mutation.Message);
        }
        if (mutation.Status == TransactionMutationStatus.Existing && mutation.Transaction != null)
        {
            RecurringOccurrenceLedgerService.SettleFromTransaction(occurrence, mutation.Transaction);
            await _context.SaveChangesAsync(cancellationToken);
        }

        var refreshed = await _context.RecurringPaymentOccurrences.AsNoTracking().SingleAsync(
            item => item.RecurringPaymentId == paymentId && item.OccurrenceDate == occurrenceDate,
            cancellationToken);
        var next = (await _occurrences.GetNextPendingAsync(
            payment, _clock.Today, includeFrom: true, cancellationToken))?.OccurrenceDate;
        return new RecurringSettlementResult(
            RecurringSettlementStatus.Success, refreshed, mutation.Transaction, next);
    }

    private static string BuildTransactionId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"tx-recurring-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
