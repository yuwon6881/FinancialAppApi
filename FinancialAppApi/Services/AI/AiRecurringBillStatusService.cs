using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.AI;

// Amount is the scheduled figure ("how much is this bill"); Outstanding is what is still owed and
// the only one of the two a projection may add up. They differ exactly when a bill is partly paid.
public sealed record AiBillStatus(
    string RecurringPaymentId,
    string Name,
    string Category,
    string LedgerCategory,
    string DueDate,
    string Status,
    decimal? Amount,
    decimal Outstanding);

// Per-cycle bill status (Paid / Pending / PartiallyPaid / Discarded) for every active recurring
// payment billed in the given cycles, read from the authoritative occurrence ledger. It mirrors
// FinancialService.BuildActiveRecurringList, including discarded bills, which every other
// assistant read deliberately excludes.
public sealed class AiRecurringBillStatusService
{
    private readonly AppDbContext _context;
    private readonly RecurringOccurrenceLedgerService _occurrences;

    public AiRecurringBillStatusService(AppDbContext context, RecurringOccurrenceLedgerService occurrences)
    {
        _context = context;
        _occurrences = occurrences;
    }

    public async Task<IReadOnlyList<AiBillStatus>> LoadAsync(
        IReadOnlyList<AiCycle> cycles,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        var recurring = await _context.RecurringPayments.AsNoTracking().Where(payment => payment.Active).ToListAsync(cancellationToken);
        if (recurring.Count == 0) return [];
        var parentAmounts = recurring.ToDictionary(payment => payment.Id, payment => payment.Amount, StringComparer.Ordinal);
        var results = new List<AiBillStatus>();
        foreach (var cycle in cycles.Distinct())
        {
            var range = AiCycleResolver.Range(cycle, cycleDay);
            var occurrences = await _occurrences.GetRangeAsync(
                recurring, range.FirstDate, range.LastDate, cancellationToken: cancellationToken);

            // Only partially paid rows need their ledger transactions read back; every other status
            // either owes its full scheduled amount or owes nothing.
            var partiallyPaid = occurrences
                .Where(occurrence => occurrence.Status == RecurringOccurrenceStatus.PartiallyPaid)
                .ToList();
            Dictionary<(string PaymentId, DateOnly Date), decimal>? paidByOccurrence = null;
            if (partiallyPaid.Count > 0)
            {
                var partialIds = partiallyPaid.Select(item => item.RecurringPaymentId).Distinct(StringComparer.Ordinal).ToList();
                var partialDates = partiallyPaid.Select(item => item.OccurrenceDate).Distinct().ToList();
                var partialTransactions = await _context.Transactions
                    .AsNoTracking()
                    .Where(transaction => transaction.RecurringPaymentId != null
                        && partialIds.Contains(transaction.RecurringPaymentId)
                        && transaction.RecurringOccurrenceDate != null
                        && partialDates.Contains(transaction.RecurringOccurrenceDate.Value))
                    .ToListAsync(cancellationToken);
                paidByOccurrence = RecurringOccurrenceAmounts.PaidByOccurrence(partialTransactions);
            }

            results.AddRange(occurrences.Select(occurrence => new AiBillStatus(
                occurrence.RecurringPaymentId,
                occurrence.Name,
                occurrence.Category ?? string.Empty,
                occurrence.LedgerCategory ?? string.Empty,
                occurrence.OccurrenceDate.ToString("yyyy-MM-dd"),
                occurrence.Status,
                occurrence.ScheduledAmount.HasValue ? Math.Abs(occurrence.ScheduledAmount.Value) : null,
                RecurringOccurrenceAmounts.Outstanding(
                    occurrence,
                    parentAmounts.GetValueOrDefault(occurrence.RecurringPaymentId),
                    paidByOccurrence))));
        }
        return results;
    }
}
