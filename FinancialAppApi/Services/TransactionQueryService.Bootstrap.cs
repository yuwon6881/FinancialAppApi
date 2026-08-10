using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services;

public partial class TransactionQueryService
{
    internal static TransactionListResult ProjectCycleTransactions(
        IEnumerable<Transaction> transactions,
        int year,
        int monthIndex,
        int cycleDay)
    {
        var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(cycleStart));
        var endExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(cycleEnd));
        var items = transactions
            .Where(transaction => transaction.LedgerCategory != "Discarded"
                && transaction.Date >= start
                && transaction.Date < endExclusive)
            .OrderByDescending(transaction => transaction.Date)
            .ThenByDescending(transaction => transaction.PostedAt)
            .ThenByDescending(transaction => transaction.Id, StringComparer.Ordinal)
            .Select(transaction => new TransactionProjection(
                transaction.Id,
                transaction.Date,
                transaction.PostedAt,
                transaction.Description,
                transaction.Category,
                transaction.LedgerCategory,
                transaction.Amount,
                transaction.StabilityRecoveryTopUpAmount,
                transaction.StabilityReloadIntent,
                transaction.RecurringPaymentId,
                transaction.RecurringOccurrenceDate,
                transaction.WishlistItemId,
                transaction.SavingsGoalId))
            .ToList();
        return new TransactionListResult(items);
    }
}
