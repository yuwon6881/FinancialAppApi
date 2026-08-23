using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class RecurringOccurrenceLedgerService
{
    private static RecurringPaymentOccurrence Snapshot(RecurringPayment payment, DateOnly date) => new()
    {
        Id = $"occ-{payment.Id}-{date:yyyyMMdd}",
        RecurringPaymentId = payment.Id,
        OccurrenceDate = date,
        Name = payment.Name,
        ScheduledAmount = Math.Abs(payment.Amount),
        Category = payment.Category,
        LedgerCategory = payment.LedgerCategory,
        AccountId = payment.AccountId,
        PaymentMode = payment.PaymentMode,
        Status = RecurringOccurrenceStatus.Pending
    };

    private async Task BackfillTaggedTransactionsAsync(
        IReadOnlyCollection<RecurringPayment> activePayments,
        IReadOnlyCollection<Transaction>? knownTransactions,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        var transactions = knownTransactions == null
            ? await _context.Transactions.AsNoTracking()
                .Where(transaction => transaction.RecurringPaymentId != null
                    && transaction.RecurringOccurrenceDate != null
                    && transaction.RecurringOccurrenceDate >= start
                    && transaction.RecurringOccurrenceDate <= end)
                .ToListAsync(cancellationToken)
            : knownTransactions
                .Where(transaction => transaction.RecurringPaymentId != null
                    && transaction.RecurringOccurrenceDate != null
                    && transaction.RecurringOccurrenceDate >= start
                    && transaction.RecurringOccurrenceDate <= end)
                .ToList();
        if (transactions.Count == 0) return;

        var modes = activePayments.ToDictionary(payment => payment.Id, payment => payment.PaymentMode);
        var unknownIds = transactions
            .Select(transaction => transaction.RecurringPaymentId!)
            .Where(id => !modes.ContainsKey(id))
            .Distinct()
            .ToList();
        if (unknownIds.Count > 0)
        {
            var extra = await _context.RecurringPayments.AsNoTracking()
                .Where(payment => unknownIds.Contains(payment.Id))
                .ToDictionaryAsync(payment => payment.Id, payment => payment.PaymentMode, cancellationToken);
            foreach (var entry in extra) modes[entry.Key] = entry.Value;
        }

        var groups = transactions
            .GroupBy(t => (PaymentId: t.RecurringPaymentId!, Date: t.RecurringOccurrenceDate!.Value));

        foreach (var group in groups)
        {
            var (paymentId, date) = group.Key;
            var groupTxs = group.ToList();
            var hasDiscarded = groupTxs.Any(t => t.Amount == 0m &&
                string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase));
            var activeTxs = groupTxs.Where(t =>
                !string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase)).ToList();
            var totalPaid = activeTxs.Sum(t => Math.Abs(t.Amount));
            var lastPaymentDate = activeTxs.Count > 0 ? activeTxs.Max(t => (DateOnly?)TransactionDate.ToDateOnly(t.Date)) : null;

            if (_known.TryGetValue((paymentId, date), out var tracked))
            {
                if (tracked.Status is not (RecurringOccurrenceStatus.Discarded or RecurringOccurrenceStatus.SettledByLoanPayoff))
                {
                    var scheduled = tracked.ScheduledAmount.GetValueOrDefault();
                    if (hasDiscarded)
                    {
                        tracked.Status = RecurringOccurrenceStatus.Discarded;
                        tracked.PaidDate = null;
                    }
                    else if (scheduled > 0m && totalPaid >= scheduled)
                    {
                        tracked.Status = RecurringOccurrenceStatus.Paid;
                        tracked.PaidDate = lastPaymentDate;
                    }
                    else if (totalPaid > 0m)
                    {
                        tracked.Status = RecurringOccurrenceStatus.PartiallyPaid;
                        tracked.PaidDate = null;
                    }
                    else
                    {
                        tracked.Status = RecurringOccurrenceStatus.Pending;
                        tracked.PaidDate = null;
                    }
                }
                continue;
            }

            var firstTx = activeTxs.FirstOrDefault() ?? groupTxs.First();
            var scheduledAmount = hasDiscarded ? null : (decimal?)Math.Abs(firstTx.Amount);
            var status = hasDiscarded
                ? RecurringOccurrenceStatus.Discarded
                : (scheduledAmount.HasValue && totalPaid >= scheduledAmount.Value && totalPaid > 0m)
                    ? RecurringOccurrenceStatus.Paid
                    : totalPaid > 0m
                        ? RecurringOccurrenceStatus.PartiallyPaid
                        : RecurringOccurrenceStatus.Pending;

            var backfilled = new RecurringPaymentOccurrence
            {
                Id = $"occ-{paymentId}-{date:yyyyMMdd}",
                UserId = firstTx.UserId,
                RecurringPaymentId = paymentId,
                OccurrenceDate = date,
                Name = firstTx.Description,
                ScheduledAmount = scheduledAmount,
                Category = firstTx.Category,
                LedgerCategory = hasDiscarded ? null : firstTx.LedgerCategory,
                AccountId = hasDiscarded ? null : firstTx.AccountId,
                PaymentMode = modes.GetValueOrDefault(paymentId) ?? RecurringPaymentMode.Manual,
                Status = status,
                PaidDate = status == RecurringOccurrenceStatus.Paid ? lastPaymentDate : null
            };
            _context.RecurringPaymentOccurrences.Add(backfilled);
            Remember(backfilled);
        }
    }

    private static DateOnly TrackingStart(RecurringPayment payment) =>
        payment.OccurrenceTrackingStartDate != default
            ? payment.OccurrenceTrackingStartDate
            : DateOnly.TryParseExact(payment.StartDate, "yyyy-MM-dd", out var start) ? start : DateOnly.MinValue;
}
