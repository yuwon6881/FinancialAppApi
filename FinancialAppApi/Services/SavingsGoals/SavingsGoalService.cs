using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

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
    ContributionInvalid,
    /// <summary>The requested earmark would claim more Rewards money than actually exists.</summary>
    ExceedsAvailable,
    AlreadyCompleted
}

public sealed record SavingsGoalResult(
    SavingsGoalMutationStatus Status,
    SavingsGoal? Goal = null,
    string? Message = null);

public sealed record SavingsGoalFundingResult(
    SavingsGoalMutationStatus Status,
    IReadOnlyList<SavingsGoal> Goals,
    decimal TotalGranted,
    decimal FreeToSpend,
    string? Message = null);

/// <summary>
/// The whole-pool picture the Rewards page renders: one balance, split into what goals have
/// claimed and what is genuinely free.
/// </summary>
public sealed record SavingsGoalPoolSummary(
    decimal RewardsBalance,
    decimal TotalEarmarked,
    decimal Unassigned,
    decimal RequiredPerCycleTotal,
    string CurrentCycleKey);

/// <summary>
/// Owns dated savings commitments funded from the Rewards pool.
///
/// Two rules hold everything together:
/// 1. Earmarks are bookkeeping on money that already exists -- nothing here writes to the ledger
///    and the four budget allocations are never touched.
/// 2. SUM(earmarked) can never exceed the Rewards balance. Every mutation that grows an earmark
///    re-checks this against a freshly computed balance, so a stale client (or a replayed offline
///    op) cannot over-commit the pool.
/// </summary>
public class SavingsGoalService
{
    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly FinancialClock _financialClock;

    public SavingsGoalService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        FinancialClock? financialClock = null)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _financialClock = financialClock ?? FinancialClock.Utc;
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
    {
        var cycleDay = await GetCycleDayAsync(cancellationToken);
        var rewardsBalance = await GetRewardsBalanceAsync(cycleDay, cancellationToken);
        var active = await _context.SavingsGoals
            .AsNoTracking()
            .Where(goal => goal.Status == SavingsGoalStatus.Active)
            .ToListAsync(cancellationToken);

        var today = _financialClock.Today;
        var totalEarmarked = active.Sum(goal => goal.EarmarkedAmount);
        var requiredPerCycleTotal = active
            .Sum(goal => SavingsGoalPacing.ComputePace(goal, today, cycleDay).RequiredPerCycle);

        return new SavingsGoalPoolSummary(
            rewardsBalance,
            totalEarmarked,
            SavingsGoalPacing.Unassigned(rewardsBalance, totalEarmarked),
            requiredPerCycleTotal,
            CurrentCycleKey(today, cycleDay));
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
        goal.LastFundedCycleKey = null;
        goal.EarmarkedAmount = Math.Max(0m, goal.EarmarkedAmount);

        // A goal may be seeded with money already set aside. That still has to fit in the pool.
        if (goal.EarmarkedAmount > 0m)
        {
            var headroom = await GetUnassignedAsync(cancellationToken: cancellationToken);
            if (goal.EarmarkedAmount > headroom)
            {
                return ExceedsAvailable(headroom);
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

        var validation = Validate(updated);
        if (validation != null) return validation;

        goal.Name = updated.Name;
        goal.TargetAmount = updated.TargetAmount;
        // Lowering the target below what is already set aside releases the surplus back to the pool
        // rather than holding money the goal no longer needs (and would breach the row-level
        // earmark <= target constraint). Raising the target never moves money.
        goal.EarmarkedAmount = Math.Min(goal.EarmarkedAmount, goal.TargetAmount);
        goal.TargetDate = updated.TargetDate;
        goal.Priority = updated.Priority;
        goal.IsRecurring = updated.IsRecurring;
        goal.RecurrenceMonths = updated.RecurrenceMonths;

        await _context.SaveChangesAsync(cancellationToken);
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
            var headroom = await GetUnassignedAsync(cancellationToken: cancellationToken);
            if (amount > headroom) return ExceedsAvailable(headroom);
        }

        // A release cannot take out more than this goal holds, and a top-up cannot push it past its
        // own target -- overshooting would quietly hold money the goal does not need.
        var next = Math.Clamp(goal.EarmarkedAmount + amount, 0m, goal.TargetAmount);
        goal.EarmarkedAmount = next;

        await _context.SaveChangesAsync(cancellationToken);
        return new SavingsGoalResult(SavingsGoalMutationStatus.Success, goal);
    }

    /// <summary>
    /// Pours the currently-unassigned Rewards money through the goal waterfall, capping each goal at
    /// its deadline-derived pace. Idempotent per cycle: goals already stamped with the current cycle
    /// key are skipped, so a second tap in the same cycle contributes nothing.
    /// </summary>
    public async Task<SavingsGoalFundingResult> FundCurrentCycleAsync(CancellationToken cancellationToken = default)
    {
        var cycleDay = await GetCycleDayAsync(cancellationToken);
        var rewardsBalance = await GetRewardsBalanceAsync(cycleDay, cancellationToken);
        var today = _financialClock.Today;
        var cycleKey = CurrentCycleKey(today, cycleDay);

        var active = await _context.SavingsGoals
            .Where(goal => goal.Status == SavingsGoalStatus.Active)
            .ToListAsync(cancellationToken);

        // Headroom is computed against EVERY active goal's earmark, including the ones skipped
        // below -- their claim on the pool stands whether or not they are funded again this cycle.
        var available = SavingsGoalPacing.Unassigned(rewardsBalance, active.Sum(goal => goal.EarmarkedAmount));
        var fundable = active.Where(goal => goal.LastFundedCycleKey != cycleKey).ToList();

        var waterfall = SavingsGoalPacing.Distribute(fundable, available, today, cycleDay);
        var byId = fundable.ToDictionary(goal => goal.Id);
        foreach (var grant in waterfall.Grants)
        {
            var goal = byId[grant.GoalId];
            goal.EarmarkedAmount = Math.Min(goal.TargetAmount, goal.EarmarkedAmount + grant.Amount);
            // Stamp even a zero grant: the cycle *was* processed, and there was nothing to give.
            // Leaving it unstamped would re-run the waterfall on every page load.
            goal.LastFundedCycleKey = cycleKey;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new SavingsGoalFundingResult(
            SavingsGoalMutationStatus.Success,
            await GetGoalsAsync(cancellationToken),
            waterfall.TotalGranted,
            waterfall.FreeToSpend);
    }

    /// <summary>
    /// Closes out a goal. The earmark is released rather than spent: no ledger row is written here,
    /// because the real outgoing (the car service, the deposit) is an ordinary Rewards expense the
    /// user logs in the ledger. A recurring goal rolls its deadline forward and starts again at zero
    /// instead of closing.
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

        if (goal.IsRecurring)
        {
            // Roll forward from the deadline that just passed, not from today, so a quarterly
            // service stays on its quarter boundaries even when marked done a week late.
            goal.TargetDate = goal.TargetDate.AddMonths(Math.Max(1, goal.RecurrenceMonths));
            goal.EarmarkedAmount = 0m;
            goal.LastFundedCycleKey = null;
        }
        else
        {
            goal.Status = SavingsGoalStatus.Completed;
            goal.CompletedAt = DateTime.UtcNow;
            goal.EarmarkedAmount = 0m;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new SavingsGoalResult(SavingsGoalMutationStatus.Success, goal);
    }

    /// <summary>
    /// Rewards money on hand right now: the balance carried into the current cycle plus this cycle's
    /// Rewards ledger movement. Mirrors how the dashboard and the AI context loader derive it, so
    /// the number the invariant is checked against is the number the user sees.
    /// </summary>
    public async Task<decimal> GetRewardsBalanceAsync(int cycleDay, CancellationToken cancellationToken = default)
    {
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

        return opening.rewards + cycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Rewards"));
    }

    private async Task<decimal> GetUnassignedAsync(int? cycleDay = null, CancellationToken cancellationToken = default)
    {
        var resolvedCycleDay = cycleDay ?? await GetCycleDayAsync(cancellationToken);
        var rewardsBalance = await GetRewardsBalanceAsync(resolvedCycleDay, cancellationToken);
        var totalEarmarked = await _context.SavingsGoals
            .Where(goal => goal.Status == SavingsGoalStatus.Active)
            .SumAsync(goal => goal.EarmarkedAmount, cancellationToken);
        return SavingsGoalPacing.Unassigned(rewardsBalance, totalEarmarked);
    }

    private async Task<int> GetCycleDayAsync(CancellationToken cancellationToken)
    {
        var setting = await _context.FinancialSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        return setting?.CycleDay ?? 28;
    }

    private static string CurrentCycleKey(DateOnly today, int cycleDay)
    {
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
        return $"{year:D4}-{monthIndex:D2}";
    }

    private static SavingsGoalResult ExceedsAvailable(decimal headroom)
    {
        return new SavingsGoalResult(
            SavingsGoalMutationStatus.ExceedsAvailable,
            Message: $"That would claim more rewards than you have. Only {headroom:0.00} is unassigned.");
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
}
