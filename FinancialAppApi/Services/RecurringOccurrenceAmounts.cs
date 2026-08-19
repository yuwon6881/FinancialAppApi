using FinancialAppApi.Models;

namespace FinancialAppApi.Services;

/// <summary>
/// Paid-so-far and still-owed for one bill occurrence.
/// </summary>
/// <remarks>
/// There is deliberately no stored PaidAmount column: the transactions tagged with an occurrence are
/// the record, and a status is only a summary of them. That makes it the readers' job to agree on
/// how those rows are summed, which they did not — six near-identical copies of this arithmetic had
/// drifted into settlement, persistence, alerts and the dashboard, and the ones that had not been
/// updated for partial payments quoted the full scheduled amount for a bill that was half paid.
/// Two rules every copy has to share: a discarded row is a marker carrying no money, and the sign of
/// a stored amount is not a contract, so magnitudes are compared rather than signed values.
/// </remarks>
public static class RecurringOccurrenceAmounts
{
    public const string DiscardedLedgerCategory = "Discarded";

    public static bool IsDiscardedMarker(Transaction transaction) =>
        string.Equals(transaction.LedgerCategory, DiscardedLedgerCategory, StringComparison.OrdinalIgnoreCase);

    public static decimal PaidSoFar(IEnumerable<Transaction> occurrenceTransactions) =>
        occurrenceTransactions.Where(transaction => !IsDiscardedMarker(transaction))
            .Sum(transaction => Math.Abs(transaction.Amount));

    public static decimal Remaining(decimal scheduledAmount, decimal paidSoFar) =>
        Math.Max(0m, Math.Abs(scheduledAmount) - paidSoFar);

    /// <summary>
    /// Total paid per (bill, occurrence date), for callers holding a batch of transactions already.
    /// Rows without both keys cannot belong to an occurrence and are skipped.
    /// </summary>
    public static Dictionary<(string PaymentId, DateOnly Date), decimal> PaidByOccurrence(
        IEnumerable<Transaction>? transactions)
    {
        var result = new Dictionary<(string, DateOnly), decimal>();
        if (transactions == null) return result;

        foreach (var transaction in transactions)
        {
            if (transaction.RecurringPaymentId == null || transaction.RecurringOccurrenceDate == null) continue;
            if (IsDiscardedMarker(transaction)) continue;

            var key = (transaction.RecurringPaymentId, transaction.RecurringOccurrenceDate.Value);
            result[key] = result.GetValueOrDefault(key) + Math.Abs(transaction.Amount);
        }

        return result;
    }

    /// <summary>
    /// What this occurrence will still take out of its account. This is the figure every commitment,
    /// shortfall and reminder surface wants: a resolved occurrence owes nothing, and a partially paid
    /// one owes only its remainder.
    /// </summary>
    public static decimal Outstanding(
        RecurringPaymentOccurrence occurrence,
        decimal parentAmount,
        IReadOnlyDictionary<(string PaymentId, DateOnly Date), decimal>? paidByOccurrence = null)
    {
        if (!RecurringOccurrenceStatus.IsUnresolved(occurrence.Status)) return 0m;

        var scheduled = Math.Abs(occurrence.ScheduledAmount ?? parentAmount);
        if (occurrence.Status != RecurringOccurrenceStatus.PartiallyPaid) return scheduled;

        var paid = paidByOccurrence?.GetValueOrDefault((occurrence.RecurringPaymentId, occurrence.OccurrenceDate)) ?? 0m;
        return Remaining(scheduled, paid);
    }
}
