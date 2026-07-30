using FinancialAppApi.Models;

namespace FinancialAppApi.Services.SavingsGoals;

/// <summary>
/// The pace a single goal needs to keep, derived from its deadline rather than from a
/// user-maintained percentage. A percentage cannot answer "does this get me there by then";
/// a deadline can, so the deadline is the input and the contribution is the output.
/// </summary>
/// <param name="GoalId">The goal this pace belongs to.</param>
/// <param name="Remaining">Target minus what is already earmarked, floored at zero.</param>
/// <param name="CyclesRemaining">
/// Whole cycles left including the current one. 1 means "due this cycle", 0 or less means the
/// deadline has already passed.
/// </param>
/// <param name="RequiredPerCycle">
/// What this goal needs each cycle from here to land on its target date, measured from where it
/// stood at the START of the current cycle so it holds still as money goes in. Collapses to the
/// whole remaining amount once the deadline is here or past -- there is no longer anything to
/// spread it over.
/// </param>
/// <param name="FundedThisCycle">
/// Net amount credited to this goal during the current cycle, from automatic funding and manual
/// top-ups alike, less releases.
/// </param>
/// <param name="OutstandingThisCycle">
/// What is still owed this cycle after <paramref name="FundedThisCycle"/>. This is what "Fund this
/// cycle" acts on, so a hand-topped-up goal reports zero and releasing money reopens exactly the
/// released amount.
/// </param>
public sealed record GoalPace(
    int GoalId,
    decimal Remaining,
    int CyclesRemaining,
    decimal RequiredPerCycle,
    decimal FundedThisCycle,
    decimal OutstandingThisCycle)
{
    public bool IsOverdue => CyclesRemaining <= 0 && Remaining > 0m;
    public bool IsFunded => Remaining <= 0m;
}

/// <summary>How much of a cycle's available money a goal actually got, and what it still lacks.</summary>
public sealed record GoalGrant(int GoalId, decimal Amount, decimal Shortfall);

/// <summary>
/// The result of pouring one cycle's available Rewards money through the goal waterfall.
/// </summary>
/// <param name="Grants">Per-goal awards, in the order they were funded.</param>
/// <param name="TotalGranted">Sum of the awards.</param>
/// <param name="FreeToSpend">
/// What survived the waterfall. This -- not the whole Rewards balance -- is the money a wishlist
/// reward can be claimed against.
/// </param>
/// <param name="TotalRequired">What every goal wanted this cycle.</param>
public sealed record GoalWaterfall(
    IReadOnlyList<GoalGrant> Grants,
    decimal TotalGranted,
    decimal FreeToSpend,
    decimal TotalRequired)
{
    /// <summary>How far short of every goal's required pace this cycle fell.</summary>
    public decimal Shortfall => Math.Max(0m, TotalRequired - TotalGranted);
}

/// <summary>
/// Pure deadline-to-contribution math shared by the service and mirrored on the client in
/// <c>lib/savingsGoals.ts</c>, the same way <c>lib/cycle.ts</c> mirrors
/// <see cref="CycleBalanceService"/>. Kept free of EF and clocks so the rounding, ordering and
/// clamping behaviour is directly testable.
/// </summary>
public static class SavingsGoalPacing
{
    /// <summary>
    /// Funding order when a cycle cannot cover every goal: priority first (that is what marking a
    /// goal High is *for* -- protecting it when money is short), then the nearest deadline, then
    /// the oldest goal. Id breaks the final tie so the order is total and stable across requests.
    /// </summary>
    public static IReadOnlyList<SavingsGoal> OrderForFunding(IEnumerable<SavingsGoal> goals)
    {
        return goals
            .OrderBy(goal => PriorityRank(goal.Priority))
            .ThenBy(goal => goal.TargetDate)
            .ThenBy(goal => goal.CreatedAt)
            .ThenBy(goal => goal.Id)
            .ToList();
    }

    public static int PriorityRank(string? priority) => priority switch
    {
        "High" => 0,
        "Medium" => 1,
        "Low" => 2,
        _ => 1
    };

    /// <summary>
    /// Whole cycles between now and the deadline, counting the current one. A goal due inside the
    /// current cycle has exactly this cycle left, so it returns 1 rather than 0 -- otherwise the
    /// last cycle before a deadline would report an infinite required contribution.
    /// </summary>
    public static int CyclesRemaining(DateOnly today, DateOnly targetDate, int cycleDay)
    {
        var (currentYear, currentMonth) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
        var (targetYear, targetMonth) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(targetDate, cycleDay);
        return (targetYear - currentYear) * 12 + (targetMonth - currentMonth) + 1;
    }

    public static GoalPace ComputePace(SavingsGoal goal, DateOnly today, int cycleDay, string currentCycleKey)
    {
        var fundedThisCycle = goal.CycleFundedKey == currentCycleKey
            ? Math.Max(0m, goal.CycleFundedAmount)
            : 0m;

        var remaining = Math.Max(0m, goal.TargetAmount - goal.EarmarkedAmount);
        var cyclesRemaining = CyclesRemaining(today, DateOnly.FromDateTime(goal.TargetDate), cycleDay);

        // The pace is measured from where the goal stood at the START of this cycle, i.e. excluding
        // whatever has already been contributed during it. Using the live remainder instead would
        // make the requirement shrink the moment you funded it, so a goal could never be "done for
        // this cycle" and the figure on screen would move every time money went in.
        var remainingAtCycleStart = Math.Max(0m, goal.TargetAmount - Math.Max(0m, goal.EarmarkedAmount - fundedThisCycle));

        // Deadline reached or passed: there are no future cycles to spread the balance over, so the
        // whole remainder is due now. Spreading it anyway would under-report the urgency.
        var requiredPerCycle = cyclesRemaining <= 1
            ? remainingAtCycleStart
            : RoundUpToCent(remainingAtCycleStart / cyclesRemaining);

        // Capped by Remaining as well: the final cycle of a goal only needs the remainder, however
        // much its nominal per-cycle pace says.
        var outstanding = Math.Clamp(requiredPerCycle - fundedThisCycle, 0m, remaining);

        return new GoalPace(goal.Id, remaining, cyclesRemaining, requiredPerCycle, fundedThisCycle, outstanding);
    }

    /// <summary>
    /// Distributes <paramref name="available"/> across the goals in funding order, capping each at
    /// what it still needs this cycle. Commitments fill before fun: whatever is left over is the
    /// free-to-spend remainder, never an implicit extra contribution to the first goal in the list.
    /// </summary>
    public static GoalWaterfall Distribute(
        IEnumerable<SavingsGoal> goals,
        decimal available,
        DateOnly today,
        int cycleDay,
        string currentCycleKey)
    {
        var ordered = OrderForFunding(goals);
        var remainingPool = Math.Max(0m, available);
        var grants = new List<GoalGrant>(ordered.Count);
        var totalRequired = 0m;
        var totalGranted = 0m;

        foreach (var goal in ordered)
        {
            var wanted = ComputePace(goal, today, cycleDay, currentCycleKey).OutstandingThisCycle;
            totalRequired += wanted;

            var granted = Math.Min(wanted, remainingPool);
            remainingPool -= granted;
            totalGranted += granted;

            grants.Add(new GoalGrant(goal.Id, granted, Math.Max(0m, wanted - granted)));
        }

        return new GoalWaterfall(grants, totalGranted, remainingPool, totalRequired);
    }

    /// <summary>
    /// Money in the Rewards pool that no goal has claimed. Floored at zero so a balance that has
    /// dropped below the outstanding earmarks (a correction, a refund reversal) reports "nothing
    /// free" rather than a negative amount.
    /// </summary>
    public static decimal Unassigned(decimal rewardsBalance, decimal totalEarmarked)
    {
        return Math.Max(0m, rewardsBalance - totalEarmarked);
    }

    // Contributions are rounded UP to the cent so that N cycles of the required amount always
    // reaches the target. Rounding down leaves a few cents short on the deadline cycle.
    private static decimal RoundUpToCent(decimal value)
    {
        return Math.Ceiling(value * 100m) / 100m;
    }
}
