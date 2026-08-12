using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Services.SavingsGoals;

public enum SavingsGoalMutationStatus
{
    Success,
    NotFound,
    IdMismatch,
    NameRequired,
    TargetInvalid,
    TargetDateInvalid,
    RecurrenceInvalid,
    FundingBucketInvalid,
    ContributionInvalid,
    /// <summary>The requested earmark would claim more money from its bucket than actually exists.</summary>
    ExceedsAvailable,
    AlreadyCompleted,
    NothingEarmarked,
    Conflict
}

public sealed record SavingsGoalResult(
    SavingsGoalMutationStatus Status,
    SavingsGoal? Goal = null,
    Transaction? CompletionTransaction = null,
    string? Message = null);

public sealed record SavingsGoalFundingResult(
    SavingsGoalMutationStatus Status,
    IReadOnlyList<SavingsGoal> Goals,
    decimal TotalGranted,
    decimal FreeToSpend,
    decimal EssentialsFreeToSpend,
    string? Message = null)
{
    public decimal RewardsFreeToSpend => FreeToSpend;
}

// OutstandingThisCycleTotal is what every active goal still needs this cycle.
// Zero means there is nothing left to fund.
/// <summary>
/// The whole-pool picture the Rewards page renders: one balance, split into what goals have
/// claimed and what is genuinely free. The balance is reduced first by pending Rewards bills,
/// because those are already spoken for even though their ledger rows do not exist yet.
/// </summary>
public sealed record SavingsGoalPoolSummary(
    decimal RewardsBalance,
    decimal TotalEarmarked,
    decimal Unassigned,
    decimal RequiredPerCycleTotal,
    decimal OutstandingThisCycleTotal,
    string CurrentCycleKey)
{
    // Kept as an alias because the Rewards pool is the long-standing API shape. The overload that
    // accepts a bucket fills the same fields for an Essentials pool and labels it here.
    public string FundingBucket { get; init; } = SavingsGoalFundingBucket.Rewards;
    public decimal Balance => RewardsBalance;
}

/// <summary>
/// Owns dated savings commitments funded from the Essentials or Rewards pool.
///
/// Two rules hold everything together:
/// 1. Earmarks are bookkeeping on money that already exists. Completing a goal consumes its
///    earmark through one linked Rewards ledger expense; authoring and funding remain ledger-neutral.
/// 2. SUM(earmarked) can never exceed the Rewards balance. Every mutation that grows an earmark
///    re-checks this against a freshly computed balance, so a stale client (or a replayed offline
///    op) cannot over-commit the pool.
/// </summary>
public class SavingsGoalService
{
    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly FinancialClock _financialClock;
    private readonly RecurringOccurrenceService _recurringOccurrenceService;
    private readonly RecurringOccurrenceLedgerService _recurringOccurrenceLedger;

    public SavingsGoalService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        FinancialClock? financialClock = null,
        RecurringOccurrenceService? recurringOccurrenceService = null,
        RecurringOccurrenceLedgerService? recurringOccurrenceLedger = null)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _financialClock = financialClock ?? FinancialClock.Utc;
        _recurringOccurrenceService = recurringOccurrenceService ??
            new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        _recurringOccurrenceLedger = recurringOccurrenceLedger ??
            new RecurringOccurrenceLedgerService(context, _recurringOccurrenceService, _financialClock);
    }

    public async Task<List<SavingsGoal>> GetGoalsAsync(CancellationToken cancellationToken = default)
    {
        var goals = await _context.SavingsGoals
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Active goals first in funding order (that is the order the UI funds and lists them in),
        // then completed ones newest-first as a history tail.
        return SavingsGoalPacing
            .OrderForFunding(goals.Where(goal => goal.Status == SavingsGoalStatus.Active))
            .Concat(goals
                .Where(goal => goal.Status != SavingsGoalStatus.Active)
                .OrderByDescending(goal => goal.CompletedAt ?? goal.CreatedAt)
                .ThenByDescending(goal => goal.Id))
            .ToList();
    }

    public async Task<SavingsGoalPoolSummary> GetPoolSummaryAsync(CancellationToken cancellationToken = default)
        => await GetPoolSummaryAsync(SavingsGoalFundingBucket.Rewards, cancellationToken);

    public async Task<SavingsGoalPoolSummary> GetPoolSummaryAsync(
        string fundingBucket,
        CancellationToken cancellationToken = default)
    {
        EnsureFundingBucket(fundingBucket);
        var cycleDay = await GetCycleDayAsync(cancellationToken);
        var bucketBalance = await GetBucketBalanceAsync(fundingBucket, cycleDay, cancellationToken);
        var pendingBucket = await GetPendingBucketRecurringAsync(fundingBucket, cycleDay, cancellationToken);
        var availableBucket = Math.Max(0m, bucketBalance - pendingBucket);
        var active = await _context.SavingsGoals
            .AsNoTracking()
            .Where(goal => goal.Status == SavingsGoalStatus.Active && goal.FundingBucket == fundingBucket)
            .ToListAsync(cancellationToken);

        var today = _financialClock.Today;
        var cycleKey = CurrentCycleKey(today, cycleDay);
        var totalEarmarked = active.Sum(goal => goal.EarmarkedAmount);
        var requiredPerCycleTotal = 0m;
        var outstandingThisCycleTotal = 0m;
        foreach (var goal in active)
        {
            var pace = SavingsGoalPacing.ComputePace(goal, today, cycleDay, cycleKey);
            requiredPerCycleTotal += pace.RequiredPerCycle;
            outstandingThisCycleTotal += pace.OutstandingThisCycle;
        }

        return new SavingsGoalPoolSummary(
            availableBucket,
            totalEarmarked,
            SavingsGoalPacing.Unassigned(availableBucket, totalEarmarked),
            requiredPerCycleTotal,
            outstandingThisCycleTotal,
            cycleKey)
        {
            FundingBucket = fundingBucket
        };
    }

    public async Task<SavingsGoalResult> CreateGoalAsync(SavingsGoal goal, CancellationToken cancellationToken = default)
    {
        // Idempotency: an offline create can be replayed after a lost response. Dedupe on the
        // client-supplied key and return the existing row rather than inserting a second goal.
        if (!string.IsNullOrWhiteSpace(goal.ClientKey))
        {
            var existing = await _context.SavingsGoals
                .FirstOrDefaultAsync(candidate => candidate.ClientKey == goal.ClientKey, cancellationToken);
            if (existing != null)
            {
                return new SavingsGoalResult(SavingsGoalMutationStatus.Success, existing);
            }
        }

        var validation = Validate(goal);
        if (validation != null) return validation;

        goal.CreatedAt = DateTime.UtcNow;
        goal.Status = SavingsGoalStatus.Active;
        goal.CompletedAt = null;
        goal.CycleFundedKey = null;
        goal.CycleFundedAmount = 0m;
        goal.EarmarkedAmount = Math.Max(0m, goal.EarmarkedAmount);
        goal.FundingBucket = string.IsNullOrWhiteSpace(goal.FundingBucket)
            ? SavingsGoalFundingBucket.Rewards
            : goal.FundingBucket;
        goal.RecurrenceDayOfMonth = goal.IsRecurring ? goal.TargetDate.Day : null;
        goal.LastCompletionTransactionId = null;

        // A goal may be seeded with money already set aside. That still has to fit in the pool.
        if (goal.EarmarkedAmount > 0m)
        {
            var headroom = await GetUnassignedAsync(goal.FundingBucket, cancellationToken: cancellationToken);
            if (goal.EarmarkedAmount > headroom)
            {
                return ExceedsAvailable(headroom, goal.FundingBucket);
            }
        }

        _context.SavingsGoals.Add(goal);
        await _context.SaveChangesAsync(cancellationToken);
        return new SavingsGoalResult(SavingsGoalMutationStatus.Success, goal);
    }

    public async Task<SavingsGoalResult> UpdateGoalAsync(
        int id,
        SavingsGoal updated,
        CancellationToken cancellationToken = default)
    {
        if (id != updated.Id)
        {
            return new SavingsGoalResult(SavingsGoalMutationStatus.IdMismatch, Message: "ID mismatch.");
        }

        var goal = await _context.SavingsGoals.FindAsync([id], cancellationToken);
        if (goal == null) return new SavingsGoalResult(SavingsGoalMutationStatus.NotFound);
        if (goal.Status != SavingsGoalStatus.Active)
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.AlreadyCompleted,
                Message: "A completed goal cannot be edited. Undo its completion from the ledger first.");
        }

        var validation = Validate(updated);
        if (validation != null) return validation;

        var nextFundingBucket = updated.FundingBucket;
        if (!string.Equals(goal.FundingBucket, nextFundingBucket, StringComparison.Ordinal)
            && goal.EarmarkedAmount > 0m)
        {
            var targetBucketHeadroom = await GetUnassignedAsync(nextFundingBucket, cancellationToken: cancellationToken);
            if (goal.EarmarkedAmount > targetBucketHeadroom)
            {
                return ExceedsAvailable(targetBucketHeadroom, nextFundingBucket);
            }
        }

        goal.Name = updated.Name;
        goal.TargetAmount = updated.TargetAmount;
        // Lowering the target below what is already set aside releases the surplus back to the pool
        // rather than holding money the goal no longer needs (and would breach the row-level
        // earmark <= target constraint). Raising the target never moves money.
        var clampedEarmark = Math.Min(goal.EarmarkedAmount, goal.TargetAmount);
        if (clampedEarmark != goal.EarmarkedAmount)
        {
            // Same rule as ContributeAsync: a release has to come off this cycle's tally too.
            // Without this the goal keeps reporting the released amount as already funded, so
            // "Fund this cycle" sees zero outstanding and refuses to top it back up.
            ApplyCycleFunding(
                goal,
                clampedEarmark - goal.EarmarkedAmount,
                await GetCurrentCycleKeyAsync(cancellationToken));
            goal.EarmarkedAmount = clampedEarmark;
        }
        var targetDateChanged = goal.TargetDate.Date != updated.TargetDate.Date;
        goal.TargetDate = updated.TargetDate;
        goal.Priority = updated.Priority;
        goal.FundingBucket = nextFundingBucket;
        goal.IsRecurring = updated.IsRecurring;
        goal.RecurrenceMonths = updated.RecurrenceMonths;
        goal.RecurrenceDayOfMonth = updated.IsRecurring
            ? targetDateChanged
                ? updated.TargetDate.Day
                : NormalizeRecurrenceDay(updated.RecurrenceDayOfMonth ?? goal.RecurrenceDayOfMonth, updated.TargetDate.Day)
            : null;
        InvalidateCompletionUndo(goal);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.Conflict,
                Message: "This commitment changed while it was being edited. Refresh and try again.");
        }
        return new SavingsGoalResult(SavingsGoalMutationStatus.Success, goal);
    }

    /// <summary>
    /// Deleting a goal releases its earmark: the money was never moved anywhere, so it simply stops
    /// being claimed and reappears as free-to-spend.
    /// </summary>
    public async Task<SavingsGoalMutationStatus> DeleteGoalAsync(int id, CancellationToken cancellationToken = default)
    {
        var goal = await _context.SavingsGoals.FindAsync([id], cancellationToken);
        if (goal == null) return SavingsGoalMutationStatus.NotFound;

        _context.SavingsGoals.Remove(goal);
        await _context.SaveChangesAsync(cancellationToken);
        return SavingsGoalMutationStatus.Success;
    }

    /// <summary>
    /// Moves money into (positive) or out of (negative) a single goal's earmark. This is the manual
    /// escape hatch for windfalls and for raiding one goal to cover another; the automatic path is
    /// <see cref="FundCurrentCycleAsync"/>.
    /// </summary>
    public async Task<SavingsGoalResult> ContributeAsync(
        int id,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        if (amount == 0m)
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.ContributionInvalid,
                Message: "Contribution amount must be a non-zero number.");
        }

        var goal = await _context.SavingsGoals.FindAsync([id], cancellationToken);
        if (goal == null) return new SavingsGoalResult(SavingsGoalMutationStatus.NotFound);
        if (goal.Status != SavingsGoalStatus.Active)
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.AlreadyCompleted,
                Message: "This goal is already completed.");
        }

        if (amount > 0m)
        {
            var headroom = await GetUnassignedAsync(goal.FundingBucket, cancellationToken: cancellationToken);
            if (amount > headroom) return ExceedsAvailable(headroom, goal.FundingBucket);
        }

        // A release cannot take out more than this goal holds, and a top-up cannot push it past its
        // own target -- overshooting would quietly hold money the goal does not need.
        var next = Math.Clamp(goal.EarmarkedAmount + amount, 0m, goal.TargetAmount);
        // Credit the *applied* delta, not the requested one, so a clamped top-up does not claim more
        // of this cycle's entitlement than it actually consumed.
        ApplyCycleFunding(goal, next - goal.EarmarkedAmount, await GetCurrentCycleKeyAsync(cancellationToken));
        if (next != goal.EarmarkedAmount) InvalidateCompletionUndo(goal);
        goal.EarmarkedAmount = next;

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
        return new SavingsGoalResult(SavingsGoalMutationStatus.Success, goal);
    }

    /// <summary>
    /// Folds a change to a goal's earmark into its per-cycle tally, resetting the tally first when it
    /// belongs to an older cycle. Manual top-ups and automatic funding both land here, which is what
    /// lets "Fund this cycle" skip a goal the user already topped up by hand.
    /// </summary>
    private static void ApplyCycleFunding(SavingsGoal goal, decimal delta, string cycleKey)
    {
        var current = goal.CycleFundedKey == cycleKey ? goal.CycleFundedAmount : 0m;
        // Floored at zero: releasing money that was set aside in an earlier cycle must not inflate
        // this cycle's entitlement into a negative tally.
        goal.CycleFundedAmount = Math.Max(0m, current + delta);
        goal.CycleFundedKey = cycleKey;
    }

    /// <summary>
    /// Pours each eligible bucket's currently-unassigned money through the goal waterfall, giving each goal only
    /// what it still needs this cycle.
    ///
    /// Precise rather than once-per-cycle: a goal the user already topped up by hand is outstanding
    /// zero and receives nothing, releasing money from a goal makes exactly that goal fundable again,
    /// and tapping twice with nothing outstanding is a no-op.
    /// </summary>
    public async Task<SavingsGoalFundingResult> FundCurrentCycleAsync(CancellationToken cancellationToken = default)
    {
        var cycleDay = await GetCycleDayAsync(cancellationToken);
        var today = _financialClock.Today;
        var cycleKey = CurrentCycleKey(today, cycleDay);

        var active = await _context.SavingsGoals
            .Where(goal => goal.Status == SavingsGoalStatus.Active)
            .ToListAsync(cancellationToken);

        var byId = active.ToDictionary(goal => goal.Id);
        var totalGranted = 0m;
        var rewardsFreeToSpend = 0m;
        var essentialsFreeToSpend = 0m;
        var rewardsAvailable = 0m;
        var essentialsAvailable = 0m;
        foreach (var bucketGroup in active.GroupBy(goal => goal.FundingBucket))
        {
            var available = await GetUnassignedAsync(bucketGroup.Key, cycleDay, cancellationToken);
            var waterfall = SavingsGoalPacing.Distribute(bucketGroup, available, today, cycleDay, cycleKey);
            totalGranted += waterfall.TotalGranted;
            if (bucketGroup.Key == SavingsGoalFundingBucket.Rewards)
            {
                rewardsAvailable = available;
                rewardsFreeToSpend = waterfall.FreeToSpend;
            }
            else if (bucketGroup.Key == SavingsGoalFundingBucket.Essentials)
            {
                essentialsAvailable = available;
                essentialsFreeToSpend = waterfall.FreeToSpend;
            }
            foreach (var grant in waterfall.Grants)
            {
                if (grant.Amount <= 0m) continue;
                var goal = byId[grant.GoalId];
                var next = Math.Min(goal.TargetAmount, goal.EarmarkedAmount + grant.Amount);
                ApplyCycleFunding(goal, next - goal.EarmarkedAmount, cycleKey);
                InvalidateCompletionUndo(goal);
                goal.EarmarkedAmount = next;
            }
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new SavingsGoalFundingResult(
                SavingsGoalMutationStatus.Conflict,
                [],
                0m,
                rewardsAvailable,
                essentialsAvailable,
                "A commitment changed while this cycle was being funded. Refresh and try again.");
        }

        return new SavingsGoalFundingResult(
            SavingsGoalMutationStatus.Success,
            await GetGoalsAsync(cancellationToken),
            totalGranted,
            rewardsFreeToSpend,
            essentialsFreeToSpend);
    }

    /// <summary>
    /// Closes out a goal by consuming exactly what it has earmarked through a linked bucket ledger
    /// expense. Deleting that transaction restores this snapshot while it is still the latest,
    /// untouched completion. A recurring goal rolls its deadline forward and starts again at zero.
    /// </summary>
    public async Task<SavingsGoalResult> CompleteGoalAsync(int id, CancellationToken cancellationToken = default)
    {
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

        var previousTargetDate = goal.TargetDate;
        var previousEarmarkedAmount = goal.EarmarkedAmount;
        var previousCycleFundedKey = goal.CycleFundedKey;
        var previousCycleFundedAmount = goal.CycleFundedAmount;
        var wasRecurring = goal.IsRecurring;
        var transactionDate = TransactionDate.FromInputDate(_financialClock.Today);
        var transaction = new Transaction
        {
            Id = $"savings-goal-completion-{goal.Id}-{Guid.NewGuid():N}",
            Date = transactionDate,
            PostedAt = DateTime.UtcNow,
            Description = $"Completed commitment: {goal.Name}",
            Category = "Other",
            LedgerCategory = goal.FundingBucket,
            Amount = -previousEarmarkedAmount,
            SavingsGoalId = goal.Id
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

    /// <summary>
    /// Eligible bucket money on hand right now: the balance carried into the current cycle plus this cycle's
    /// bucket ledger movement. Mirrors how the dashboard and the AI context loader derive it, so
    /// the number the invariant is checked against is the number the user sees.
    /// </summary>
    public Task<decimal> GetRewardsBalanceAsync(int cycleDay, CancellationToken cancellationToken = default)
        => GetBucketBalanceAsync(SavingsGoalFundingBucket.Rewards, cycleDay, cancellationToken);

    public async Task<decimal> GetBucketBalanceAsync(
        string fundingBucket,
        int cycleDay,
        CancellationToken cancellationToken = default)
    {
        EnsureFundingBucket(fundingBucket);
        var today = _financialClock.Today;
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
        var opening = await _cycleBalanceService.GetOpeningBalanceAsync(year, monthIndex, cycleDay);

        var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        var startInclusive = TransactionDate.StartOfDate(DateOnly.FromDateTime(cycleStart));
        var endExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(cycleEnd));

        var cycleTxs = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= startInclusive && t.Date < endExclusive)
            .ToListAsync(cancellationToken);

        var openingBalance = fundingBucket == SavingsGoalFundingBucket.Essentials
            ? opening.essentials
            : opening.rewards;
        return openingBalance + cycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, fundingBucket));
    }

    private async Task<decimal> GetUnassignedAsync(
        string fundingBucket = SavingsGoalFundingBucket.Rewards,
        int? cycleDay = null,
        CancellationToken cancellationToken = default)
    {
        EnsureFundingBucket(fundingBucket);
        var resolvedCycleDay = cycleDay ?? await GetCycleDayAsync(cancellationToken);
        var bucketBalance = await GetBucketBalanceAsync(fundingBucket, resolvedCycleDay, cancellationToken);
        var pendingBucket = await GetPendingBucketRecurringAsync(fundingBucket, resolvedCycleDay, cancellationToken);
        var totalEarmarked = await _context.SavingsGoals
            .Where(goal => goal.Status == SavingsGoalStatus.Active && goal.FundingBucket == fundingBucket)
            .SumAsync(goal => goal.EarmarkedAmount, cancellationToken);
        return SavingsGoalPacing.Unassigned(bucketBalance, totalEarmarked, pendingBucket);
    }

    /// <summary>
    /// Holds an unsettled Rewards subscription out of the same pool that goals and wishlist claims
    /// use. Occurrence-tagged transactions are matched by their exact occurrence date; untagged
    /// transactions retain the legacy posting-date fallback because older ledger rows predate the
    /// occurrence column.
    /// </summary>
    private async Task<decimal> GetPendingBucketRecurringAsync(
        string fundingBucket,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        EnsureFundingBucket(fundingBucket);
        var today = _financialClock.Today;
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
        var range = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        var startOnly = DateOnly.FromDateTime(range.start);
        var endOnly = DateOnly.FromDateTime(range.end);
        var payments = await _context.RecurringPayments
            .AsNoTracking()
            .Where(payment => payment.Active)
            .ToListAsync(cancellationToken);

        var occurrences = await _recurringOccurrenceLedger.GetRangeAsync(
            payments, startOnly, endOnly, cancellationToken: cancellationToken);
        var pending = occurrences
            .Where(occurrence => occurrence.Status == RecurringOccurrenceStatus.Pending
                && string.Equals(occurrence.LedgerCategory, fundingBucket, StringComparison.OrdinalIgnoreCase))
            .Sum(occurrence => Math.Abs(occurrence.ScheduledAmount ?? 0m));

        return Math.Round(pending, 2, MidpointRounding.AwayFromZero);
    }

    private async Task<int> GetCycleDayAsync(CancellationToken cancellationToken)
    {
        var setting = await _context.FinancialSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        return setting?.CycleDay ?? FinancialConstants.DefaultCycleDay;
    }

    private async Task<string> GetCurrentCycleKeyAsync(CancellationToken cancellationToken)
    {
        return CurrentCycleKey(_financialClock.Today, await GetCycleDayAsync(cancellationToken));
    }

    private static string CurrentCycleKey(DateOnly today, int cycleDay)
    {
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
        return $"{year:D4}-{monthIndex:D2}";
    }

    private static int NormalizeRecurrenceDay(int? recurrenceDay, int fallbackDay)
    {
        return Math.Clamp(recurrenceDay ?? fallbackDay, 1, 31);
    }

    private static DateTime AddRecurringPeriod(DateTime currentDate, int recurrenceMonths, int recurrenceDay)
    {
        var nextMonth = currentDate.AddMonths(recurrenceMonths);
        var day = Math.Min(recurrenceDay, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
        return new DateTime(nextMonth.Year, nextMonth.Month, day);
    }

    private static SavingsGoalResult ExceedsAvailable(decimal headroom, string fundingBucket)
    {
        var bucketLabel = fundingBucket == SavingsGoalFundingBucket.Essentials ? "essentials" : "rewards";
        return new SavingsGoalResult(
            SavingsGoalMutationStatus.ExceedsAvailable,
            Message: $"That would claim more {bucketLabel} money than you have. Only {headroom:0.00} is unassigned.");
    }

    private static SavingsGoalResult? Validate(SavingsGoal goal)
    {
        if (string.IsNullOrWhiteSpace(goal.Name))
        {
            return new SavingsGoalResult(SavingsGoalMutationStatus.NameRequired, Message: "Goal name is required.");
        }
        if (goal.TargetAmount <= 0m)
        {
            return new SavingsGoalResult(SavingsGoalMutationStatus.TargetInvalid, Message: "Goal target must be greater than zero.");
        }
        if (goal.TargetDate == default)
        {
            return new SavingsGoalResult(SavingsGoalMutationStatus.TargetDateInvalid, Message: "Goal target date is required.");
        }
        if (!IsAllowedFundingBucket(goal.FundingBucket))
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.FundingBucketInvalid,
                Message: "Savings goals can use Essentials or Rewards only; Growth belongs to investments and Stability is reserved for emergency-fund recovery.");
        }
        if (goal.IsRecurring && (goal.RecurrenceMonths < 1 || goal.RecurrenceMonths > 120))
        {
            return new SavingsGoalResult(SavingsGoalMutationStatus.RecurrenceInvalid, Message: "Repeat interval must be between 1 and 120 months.");
        }
        if (SavingsGoalPacing.PriorityRank(goal.Priority) == 1 && goal.Priority is not ("Medium" or null))
        {
            // Unknown priority strings would silently rank as Medium; normalise instead of guessing.
            goal.Priority = "Medium";
        }

        return null;
    }

    private static bool IsAllowedFundingBucket(string? fundingBucket) =>
        fundingBucket is SavingsGoalFundingBucket.Essentials or SavingsGoalFundingBucket.Rewards;

    private static void EnsureFundingBucket(string fundingBucket)
    {
        if (!IsAllowedFundingBucket(fundingBucket))
        {
            throw new ArgumentException("Savings goals can use Essentials or Rewards only; Growth belongs to investments and Stability is reserved for emergency-fund recovery.", nameof(fundingBucket));
        }
    }
}
