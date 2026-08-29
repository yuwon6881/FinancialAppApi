using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.SavingsGoals;

public partial class SavingsGoalService
{
    /// <summary>
    /// Closes out a goal by consuming exactly what it has earmarked through a linked bucket ledger
    /// expense. Deleting that transaction restores this snapshot while it is still the latest,
    /// untouched completion. A recurring goal rolls its deadline forward and starts again at zero.
    /// </summary>
    public Task<SavingsGoalResult> CompleteGoalAsync(
        int id,
        string? accountId,
        CancellationToken cancellationToken = default)
        => CompleteGoalAsync(id, accountId, null, null, cancellationToken);

    public async Task<SavingsGoalResult> CompleteGoalAsync(
        int id,
        string? accountId,
        string? transactionId,
        DateTime? postedAt,
        CancellationToken cancellationToken)
    {
        await using var poolLock = await _sharedPoolMutationLock.AcquireAsync(cancellationToken);
        var requestedTransactionId = string.IsNullOrWhiteSpace(transactionId) ? null : transactionId.Trim();
        if (requestedTransactionId is { Length: > 200 })
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.Conflict,
                Message: "The completion transaction ID is invalid.");
        }

        if (requestedTransactionId != null)
        {
            var retainedCompletion = await _context.SavingsGoalCompletions
                .FirstOrDefaultAsync(completion => completion.TransactionId == requestedTransactionId, cancellationToken);
            var existingTransaction = await _context.Transactions
                .FirstOrDefaultAsync(transaction => transaction.Id == requestedTransactionId, cancellationToken);
            if (existingTransaction != null)
            {
                var existingGoal = await _context.SavingsGoals.FindAsync([id], cancellationToken);
                if (retainedCompletion?.SavingsGoalId == id
                    && retainedCompletion.ReversedAt == null
                    && existingTransaction.SavingsGoalId == id
                    && existingGoal != null)
                {
                    return new SavingsGoalResult(SavingsGoalMutationStatus.Success, existingGoal, existingTransaction);
                }

                return new SavingsGoalResult(
                    SavingsGoalMutationStatus.Conflict,
                    Message: "That completion transaction ID is already in use.");
            }
            if (retainedCompletion != null)
            {
                return new SavingsGoalResult(
                    SavingsGoalMutationStatus.Conflict,
                    Message: "That completion transaction ID belongs to a completion that was already reversed.");
            }
        }

        var goal = await _context.SavingsGoals.FindAsync([id], cancellationToken);
        if (goal == null) return new SavingsGoalResult(SavingsGoalMutationStatus.NotFound);
        if (goal.Status != SavingsGoalStatus.Active)
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.AlreadyCompleted,
                Message: "This goal is already completed.");
        }
        if (goal.EarmarkedAmount <= 0m)
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.NothingEarmarked,
                Message: $"Set aside some {goal.FundingBucket.ToLowerInvariant()} money before marking this commitment done.");
        }

        var coverageShortfall = await GetCommitmentCoverageShortfallAsync(
            goal.FundingBucket,
            cancellationToken);
        if (coverageShortfall > 0m)
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.ExceedsAvailable,
                goal,
                Message: $"This commitment is no longer fully backed by the {goal.FundingBucket} pool. Restore {coverageShortfall:0.00} before marking it done.");
        }

        var requestedAccountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
        if (requestedAccountId is null)
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.InvalidAccount,
                Message: "Choose an account for this commitment.",
                Code: "ledger_account_required",
                MissingBuckets: [goal.FundingBucket]);
        var account = await _context.LedgerAccounts.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == requestedAccountId, cancellationToken);
        if (account is null || account.IsArchived
            || !account.Bucket.Equals(goal.FundingBucket, StringComparison.OrdinalIgnoreCase))
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.InvalidAccount,
                Message: "Choose an open account in the commitment's bucket.",
                Code: "ledger_account_invalid",
                MissingBuckets: [goal.FundingBucket]);

        var previousTargetDate = goal.TargetDate;
        var previousEarmarkedAmount = goal.EarmarkedAmount;
        var previousCycleFundedKey = goal.CycleFundedKey;
        var previousCycleFundedAmount = goal.CycleFundedAmount;
        var wasRecurring = goal.IsRecurring;
        var transactionDate = TransactionDate.FromInputDate(_financialClock.Today);
        var transaction = new Transaction
        {
            Id = requestedTransactionId ?? $"savings-goal-completion-{goal.Id}-{Guid.NewGuid():N}",
            Date = transactionDate,
            PostedAt = postedAt?.ToUniversalTime() ?? DateTime.UtcNow,
            Description = $"Completed commitment: {goal.Name}",
            Category = "Other",
            LedgerCategory = goal.FundingBucket,
            Amount = -previousEarmarkedAmount,
            ExcludeFromAutocomplete = true,
            SavingsGoalId = goal.Id,
            AccountId = requestedAccountId,
        };

        if (goal.IsRecurring)
        {
            // Roll forward from the deadline that just passed, not from today, so a quarterly
            // service stays on its quarter boundaries even when marked done a week late. Keep the
            // original day separately because DateTime.AddMonths clamps (for example) January 31
            // to February 28 and would otherwise make every later deadline the 28th.
            var recurrenceMonths = Math.Max(1, goal.RecurrenceMonths);
            var recurrenceDay = NormalizeRecurrenceDay(goal.RecurrenceDayOfMonth, goal.TargetDate.Day);
            var nextTargetDate = AddRecurringPeriod(goal.TargetDate, recurrenceMonths, recurrenceDay);

            // Completing a stale goal represents one completed period. Skip any additional missed
            // periods so the next active deadline is actionable instead of leaving the goal overdue
            // and forcing the user to press Complete repeatedly.
            while (DateOnly.FromDateTime(nextTargetDate) <= _financialClock.Today)
            {
                nextTargetDate = AddRecurringPeriod(nextTargetDate, recurrenceMonths, recurrenceDay);
            }

            goal.TargetDate = nextTargetDate;
            goal.RecurrenceDayOfMonth = recurrenceDay;
            goal.EarmarkedAmount = 0m;
            // Cleared, not decremented: the new period starts fresh and should be fundable at once.
            goal.CycleFundedKey = null;
            goal.CycleFundedAmount = 0m;
        }
        else
        {
            goal.Status = SavingsGoalStatus.Completed;
            goal.CompletedAt = DateTime.UtcNow;
            goal.EarmarkedAmount = 0m;
            goal.CycleFundedKey = null;
            goal.CycleFundedAmount = 0m;
        }

        goal.LastCompletionTransactionId = transaction.Id;
        _context.Transactions.Add(transaction);
        _context.SavingsGoalCompletions.Add(new SavingsGoalCompletion
        {
            TransactionId = transaction.Id,
            SavingsGoalId = goal.Id,
            PreviousTargetDate = previousTargetDate,
            PreviousEarmarkedAmount = previousEarmarkedAmount,
            PreviousCycleFundedKey = previousCycleFundedKey,
            PreviousCycleFundedAmount = previousCycleFundedAmount,
            ResultingTargetDate = goal.TargetDate,
            WasRecurring = wasRecurring,
            CreatedAt = DateTime.UtcNow
        });

        try
        {
            await SaveCompletionAndInvalidateAsync(transactionDate, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.Conflict,
                Message: "This commitment changed while it was being completed. Refresh and try again.");
        }
        return new SavingsGoalResult(SavingsGoalMutationStatus.Success, goal, transaction);
    }

    private async Task SaveCompletionAndInvalidateAsync(DateTime transactionDate, CancellationToken cancellationToken)
    {
        var cycleDay = await GetCycleDayAsync(cancellationToken);
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            TransactionDate.ToDateOnly(transactionDate),
            cycleDay);

        if (!_context.Database.IsRelational())
        {
            await _context.SaveChangesAsync(cancellationToken);
            await _cycleBalanceService.InvalidateFromAsync(year, monthIndex, cancellationToken);
            return;
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await _cycleBalanceService.InvalidateFromAsync(year, monthIndex, cancellationToken);
            await dbTransaction.CommitAsync(cancellationToken);
        });
    }

    private static void InvalidateCompletionUndo(SavingsGoal goal)
    {
        goal.LastCompletionTransactionId = null;
    }
}
