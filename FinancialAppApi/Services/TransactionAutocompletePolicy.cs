using FinancialAppApi.Models;

namespace FinancialAppApi.Services;

/// <summary>
/// Identifies ledger rows that describe application bookkeeping rather than a reusable user
/// description. The decision is structural and deliberately does not inspect display text.
/// </summary>
public static class TransactionAutocompletePolicy
{
    public static bool ShouldExclude(Transaction transaction) => ShouldExclude(
        transaction.Id,
        transaction.Category,
        transaction.LedgerCategory,
        transaction.WishlistItemId,
        transaction.SavingsGoalId);

    public static bool ShouldExclude(
        string? id,
        string? category,
        string? ledgerCategory,
        int? wishlistItemId,
        int? savingsGoalId)
    {
        return TransactionReportSemantics.IsTransfer(category, ledgerCategory)
            || TransactionReportSemantics.IsBalanceAdjustment(category)
            || TransactionReportSemantics.IsDiscarded(ledgerCategory)
            || (id?.Contains("-split-", StringComparison.OrdinalIgnoreCase) ?? false)
            || wishlistItemId.HasValue
            || savingsGoalId.HasValue;
    }
}
