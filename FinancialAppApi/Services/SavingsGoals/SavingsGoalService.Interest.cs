using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.SavingsGoals;

public partial class SavingsGoalService
{
    /// <summary>
    /// Records growth from pocket interest: credits the adjustment inflow to the user's ledger account
    /// in the goal's funding bucket and raises the goal's earmark by the same amount. Free-to-spend
    /// (unassigned) remains unchanged while the ledger reflects the real pocket balance.
    /// </summary>
    public async Task<SavingsGoalResult> RecordInterestAsync(
        int id,
        decimal amount,
        string? accountId,
        string? operationId,
        CancellationToken cancellationToken = default)
    {
        await using var poolLock = await _sharedPoolMutationLock.AcquireAsync(cancellationToken);

        if (amount <= 0m)
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.ContributionInvalid,
                Message: "Interest amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(operationId)
            || operationId.Length > 100
            || operationId.Any(char.IsWhiteSpace))
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.ContributionInvalid,
                Message: "A valid operation ID is required.");
        }

        var goal = await _context.SavingsGoals.FindAsync([id], cancellationToken);
        if (goal == null) return new SavingsGoalResult(SavingsGoalMutationStatus.NotFound);
        if (goal.Status != SavingsGoalStatus.Active)
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.AlreadyCompleted,
                Message: "This goal is already completed.");
        }

        var requestedAccountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
        if (requestedAccountId is null)
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.InvalidAccount,
                Message: $"Choose an account in the {goal.FundingBucket} bucket.");
        }

        var account = await _context.LedgerAccounts
            .FirstOrDefaultAsync(candidate => candidate.Id == requestedAccountId, cancellationToken);
        if (account is null || account.IsArchived
            || !string.Equals(account.Bucket, goal.FundingBucket, StringComparison.OrdinalIgnoreCase))
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.InvalidAccount,
                Message: $"Choose an account in the {goal.FundingBucket} bucket.");
        }

        if (LedgerAccountKind.IsCreditCard(account.Kind))
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.InvalidAccount,
                Message: "A credit card account cannot receive interest.");
        }

        var transactionId = $"goal-interest-{operationId}";
        var existingTransaction = await _context.Transactions
            .FirstOrDefaultAsync(t => t.Id == transactionId, cancellationToken);

        if (existingTransaction != null)
        {
            var matches = existingTransaction.AccountId == account.Id
                && string.Equals(existingTransaction.LedgerCategory, goal.FundingBucket, StringComparison.OrdinalIgnoreCase)
                && existingTransaction.Amount == amount
                && existingTransaction.IsAccountBalanceAdjustment
                && string.Equals(existingTransaction.Category, "Adjustment", StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                return new SavingsGoalResult(SavingsGoalMutationStatus.Success, goal);
            }

            return new SavingsGoalResult(
                SavingsGoalMutationStatus.Conflict,
                Message: "A transaction with this operation ID already exists with different details.");
        }

        if (goal.EarmarkedAmount + amount > goal.TargetAmount)
        {
            var room = Math.Max(0m, goal.TargetAmount - goal.EarmarkedAmount);
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.Conflict,
                Message: $"Only RM {room:0.##} of room is left before the target. Raise the target first.");
        }

        var transaction = new Transaction
        {
            Id = transactionId,
            UserId = _context.RequireCurrentUserId(),
            Date = TransactionDate.StartOfDate(_financialClock.Today),
            PostedAt = DateTime.UtcNow,
            Description = $"Interest earned - {goal.Name}",
            Category = "Adjustment",
            LedgerCategory = goal.FundingBucket,
            Amount = amount,
            ExcludeFromAutocomplete = true,
            IsAccountBalanceAdjustment = true,
            AccountId = account.Id,
            StabilityReloadIntent = StabilityReloadIntent.NotRequired,
        };

        goal.EarmarkedAmount += amount;
        InvalidateCompletionUndo(goal);

        _context.Transactions.Add(transaction);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.Conflict,
                Message: "This commitment changed while money was being moved. Refresh and try again.");
        }

        var cycleDay = await GetCycleDayAsync(cancellationToken);
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            _financialClock.Today,
            cycleDay);
        await _cycleBalanceService.InvalidateFromAsync(year, monthIndex, cancellationToken);

        return new SavingsGoalResult(SavingsGoalMutationStatus.Success, goal);
    }
}
