using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class TransactionPersistenceService
{
    private async Task<TransactionMutationResult> RestoreWishlistPurchaseAsync(
        TransactionMutationRequest request,
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        var item = await _context.WishlistItems
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == transaction.WishlistItemId, cancellationToken);
        if (item == null)
        {
            return new TransactionMutationResult(
                TransactionMutationStatus.Conflict,
                Message: "The linked reward no longer exists.");
        }

        if (item.IsPurchased && !string.Equals(item.PurchaseTransactionId, transaction.Id, StringComparison.Ordinal))
        {
            return new TransactionMutationResult(
                TransactionMutationStatus.Conflict,
                Message: "This reward has already been claimed.");
        }

        var hasCanonicalShape =
            string.Equals(transaction.Category, "Other", StringComparison.OrdinalIgnoreCase)
            && string.Equals(transaction.LedgerCategory, "Rewards", StringComparison.OrdinalIgnoreCase)
            && transaction.Amount == -item.Price
            && string.IsNullOrWhiteSpace(transaction.RecurringPaymentId)
            && transaction.StabilityRecoveryTopUpAmount.GetValueOrDefault() == 0m
            && string.IsNullOrWhiteSpace(transaction.CounterAccountId)
            && (request.SplitAccountIds == null || request.SplitAccountIds.Count == 0);
        if (!hasCanonicalShape)
        {
            return new TransactionMutationResult(
                TransactionMutationStatus.Conflict,
                Message: "The reward claim no longer matches the saved reward. Claim it again from the Rewards plan.");
        }

        var result = await _wishlistService.PurchaseWishlistItemAsync(
            item.Id,
            transaction.Date,
            cancellationToken,
            transaction.Id,
            transaction.PostedAt,
            transaction.AccountId);
        return result.Status switch
        {
            WishlistMutationStatus.Success => new TransactionMutationResult(
                TransactionMutationStatus.Created,
                result.Transaction),
            WishlistMutationStatus.InvalidAccount => new TransactionMutationResult(
                TransactionMutationStatus.InvalidAccount,
                Message: result.Message,
                Code: result.Code,
                MissingBuckets: result.MissingBuckets),
            WishlistMutationStatus.DateInvalid => new TransactionMutationResult(
                TransactionMutationStatus.InvalidDate,
                Message: result.Message),
            _ => new TransactionMutationResult(
                TransactionMutationStatus.Conflict,
                Message: result.Message ?? "The reward claim could not be restored."),
        };
    }

    private async Task ApplyWishlistPurchaseLinkAsync(
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        if (!transaction.WishlistItemId.HasValue) return;

        var item = await _context.WishlistItems.FindAsync(
            [transaction.WishlistItemId.Value],
            cancellationToken);
        if (item == null) return;

        item.IsPurchased = true;
        item.PurchasedAt = transaction.Date;
        item.PurchaseTransactionId = transaction.Id;
        item.IsActive = false;
    }

    private async Task ClearWishlistPurchaseLinkAsync(
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        WishlistItem? item = null;

        if (transaction.WishlistItemId.HasValue)
        {
            item = await _context.WishlistItems.FindAsync(
                [transaction.WishlistItemId.Value],
                cancellationToken);
        }

        item ??= await _context.WishlistItems
            .FirstOrDefaultAsync(
                w => w.PurchaseTransactionId == transaction.Id,
                cancellationToken);

        if (item == null) return;

        item.IsPurchased = false;
        item.PurchasedAt = null;
        item.PurchaseTransactionId = null;

        var hasActiveUnpurchased = await _context.WishlistItems
            .AnyAsync(
                w => w.Id != item.Id && !w.IsPurchased && w.IsActive,
                cancellationToken);
        if (!hasActiveUnpurchased)
        {
            item.IsActive = true;
        }
    }

    private async Task<string?> RestoreSavingsGoalCompletionAsync(
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        if (!transaction.SavingsGoalId.HasValue) return null;

        var completion = await _context.SavingsGoalCompletions
            .FirstOrDefaultAsync(item => item.TransactionId == transaction.Id, cancellationToken);
        if (completion == null) return null;

        var goal = await _context.SavingsGoals.FindAsync([completion.SavingsGoalId], cancellationToken);
        if (goal != null)
        {
            var expectedStatus = completion.WasRecurring
                ? SavingsGoalStatus.Active
                : SavingsGoalStatus.Completed;
            var isUntouchedLatestCompletion =
                goal.LastCompletionTransactionId == transaction.Id &&
                goal.Status == expectedStatus &&
                goal.TargetDate.Date == completion.ResultingTargetDate.Date &&
                goal.EarmarkedAmount == 0m &&
                goal.CycleFundedKey == null &&
                goal.CycleFundedAmount == 0m;

            if (!isUntouchedLatestCompletion)
            {
                return "This completion can no longer restore the commitment because it has newer changes. Keep the completion entry or delete the commitment separately.";
            }

            goal.TargetDate = completion.PreviousTargetDate;
            goal.EarmarkedAmount = completion.PreviousEarmarkedAmount;
            goal.CycleFundedKey = completion.PreviousCycleFundedKey;
            goal.CycleFundedAmount = completion.PreviousCycleFundedAmount;
            goal.Status = SavingsGoalStatus.Active;
            goal.CompletedAt = null;
            goal.LastCompletionTransactionId = null;
        }

        completion.ReversedAt = DateTime.UtcNow;
        return null;
    }
}
