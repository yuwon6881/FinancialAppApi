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
    InvalidAccount,
    Conflict
}

public sealed record SavingsGoalResult(
    SavingsGoalMutationStatus Status,
    SavingsGoal? Goal = null,
    Transaction? CompletionTransaction = null,
    string? Message = null,
    string? Code = null,
    IReadOnlyList<string>? MissingBuckets = null);

public sealed record SavingsGoalFundingResult(
    SavingsGoalMutationStatus Status,
    IReadOnlyList<SavingsGoal> Goals,
    decimal TotalGranted,
    decimal FreeToSpend,
    decimal EssentialsFreeToSpend,
    string? Message = null,
    string? ActionId = null)
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
    ///    earmark through one linked bucket ledger expense; authoring and funding remain ledger-neutral.
    /// 2. SUM(earmarked) can never exceed its bucket balance. Every mutation that grows an earmark
    ///    re-checks this against a freshly computed balance while holding the shared user lock, so
    ///    a stale client (or a replayed offline op) cannot over-commit the pool.
/// </summary>
public partial class SavingsGoalService
{
    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly FinancialClock _financialClock;
    private readonly RecurringOccurrenceService _recurringOccurrenceService;
    private readonly RecurringOccurrenceLedgerService _recurringOccurrenceLedger;
    private readonly ISharedPoolMutationLock _sharedPoolMutationLock;

    public SavingsGoalService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        FinancialClock? financialClock = null,
        RecurringOccurrenceService? recurringOccurrenceService = null,
        RecurringOccurrenceLedgerService? recurringOccurrenceLedger = null,
        ISharedPoolMutationLock? sharedPoolMutationLock = null)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _financialClock = financialClock ?? FinancialClock.Utc;
        _recurringOccurrenceService = recurringOccurrenceService ??
            new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        _recurringOccurrenceLedger = recurringOccurrenceLedger ??
            new RecurringOccurrenceLedgerService(context, _recurringOccurrenceService, _financialClock);
        _sharedPoolMutationLock = sharedPoolMutationLock ?? new SharedPoolMutationLock(context);
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
        await using var poolLock = await _sharedPoolMutationLock.AcquireAsync(cancellationToken);

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

    /// <summary>
    /// Restores a deleted goal from its Undo snapshot. This is separate from ordinary creation so
    /// the server can preserve the cycle-funding tally without allowing normal create callers to
    /// forge it. A client key makes a lost-response replay return the first restored row.
    /// </summary>
    public async Task<SavingsGoalResult> RestoreDeletedGoalAsync(
        SavingsGoal snapshot,
        CancellationToken cancellationToken = default)
    {
        await using var poolLock = await _sharedPoolMutationLock.AcquireAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(snapshot.ClientKey))
            return new SavingsGoalResult(SavingsGoalMutationStatus.Conflict, Message: "A restore key is required.");

        var existing = await _context.SavingsGoals
            .FirstOrDefaultAsync(goal => goal.ClientKey == snapshot.ClientKey, cancellationToken);
        if (existing != null) return new SavingsGoalResult(SavingsGoalMutationStatus.Success, existing);

        var validation = Validate(snapshot);
        if (validation != null) return validation;
        snapshot.Id = 0;
        snapshot.Status = SavingsGoalStatus.Active;
        snapshot.CompletedAt = null;
        snapshot.LastCompletionTransactionId = null;
        snapshot.EarmarkedAmount = Math.Clamp(snapshot.EarmarkedAmount, 0m, snapshot.TargetAmount);
        snapshot.CycleFundedAmount = Math.Clamp(snapshot.CycleFundedAmount, 0m, snapshot.EarmarkedAmount);
        snapshot.CreatedAt = snapshot.CreatedAt == default ? DateTime.UtcNow : snapshot.CreatedAt.ToUniversalTime();
        snapshot.RecurrenceDayOfMonth = snapshot.IsRecurring ? snapshot.TargetDate.Day : null;

        if (snapshot.EarmarkedAmount > 0m)
        {
            var headroom = await GetUnassignedAsync(snapshot.FundingBucket, cancellationToken: cancellationToken);
            if (snapshot.EarmarkedAmount > headroom) return ExceedsAvailable(headroom, snapshot.FundingBucket);
        }

        _context.SavingsGoals.Add(snapshot);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            _context.Entry(snapshot).State = EntityState.Detached;
            var replay = await _context.SavingsGoals
                .AsNoTracking()
                .FirstOrDefaultAsync(goal => goal.ClientKey == snapshot.ClientKey, cancellationToken);
            return replay != null
                ? new SavingsGoalResult(SavingsGoalMutationStatus.Success, replay)
                : new SavingsGoalResult(SavingsGoalMutationStatus.Conflict, Message: "This commitment restore conflicted with another change.");
        }
        return new SavingsGoalResult(SavingsGoalMutationStatus.Success, snapshot);
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

        await using var poolLock = await _sharedPoolMutationLock.AcquireAsync(cancellationToken);
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
        // Validate the amount that will actually remain earmarked. Checking the old earmark first
        // falsely rejects a move that also lowers the target to an amount the destination can hold.
        var nextEarmark = Math.Min(goal.EarmarkedAmount, updated.TargetAmount);
        if (!string.Equals(goal.FundingBucket, nextFundingBucket, StringComparison.Ordinal)
            && nextEarmark > 0m)
        {
            var targetBucketHeadroom = await GetUnassignedAsync(nextFundingBucket, cancellationToken: cancellationToken);
            if (nextEarmark > targetBucketHeadroom)
            {
                return ExceedsAvailable(targetBucketHeadroom, nextFundingBucket);
            }
        }

        goal.Name = updated.Name;
        goal.TargetAmount = updated.TargetAmount;
        // Lowering the target below what is already set aside releases the surplus back to the pool
        // rather than holding money the goal no longer needs (and would breach the row-level
        // earmark <= target constraint). Raising the target never moves money.
        var clampedEarmark = nextEarmark;
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
        await using var poolLock = await _sharedPoolMutationLock.AcquireAsync(cancellationToken);
        var goal = await _context.SavingsGoals.FindAsync([id], cancellationToken);
        if (goal == null) return SavingsGoalMutationStatus.NotFound;

        _context.SavingsGoals.Remove(goal);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SavingsGoalMutationStatus.Conflict;
        }

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

        await using var poolLock = await _sharedPoolMutationLock.AcquireAsync(cancellationToken);
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
}
