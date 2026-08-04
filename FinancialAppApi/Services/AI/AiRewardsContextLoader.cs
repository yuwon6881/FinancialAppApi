using System.Globalization;
using FinancialAppApi.Models;
using FinancialAppApi.Services.SavingsGoals;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private async Task<object?> BuildRewardsContextAsync(
        AiQueryPlan queryPlan,
        Models.FinancialSetting? setting,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        IReadOnlyList<AiTransactionRow> transactions,
        bool sensitiveMode,
        CancellationToken cancellationToken)
    {
        if (!queryPlan.NeedsRewards) return null;

        var goals = await _savingsGoalService.GetGoalsAsync(cancellationToken);
        var pool = await _savingsGoalService.GetPoolSummaryAsync(cancellationToken);
        if (sensitiveMode)
        {
            return new
            {
                available = false,
                reason = "Rewards amounts and goal history are hidden while sensitive mode is active.",
                currentCycle = pool.CurrentCycleKey,
                goalCount = goals.Count,
                historyRedacted = true
            };
        }

        var currentCycle = pool.CurrentCycleKey;
        var paceByGoal = goals
            .Where(goal => goal.Status == SavingsGoalStatus.Active)
            .Select(goal =>
            {
                var pace = SavingsGoalPacing.ComputePace(
                    goal,
                    _financialClock.Today,
                    cycleDay,
                    currentCycle);
                return new
                {
                    id = goal.Id,
                    name = goal.Name,
                    targetAmount = goal.TargetAmount,
                    earmarkedAmount = goal.EarmarkedAmount,
                    remaining = pace.Remaining,
                    targetDate = goal.TargetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    priority = GoalPriorityLabel(goal.Priority),
                    status = "active",
                    recurring = goal.IsRecurring,
                    pacePerCycle = pace.RequiredPerCycle,
                    fundedThisCycle = pace.FundedThisCycle,
                    outstandingThisCycle = pace.OutstandingThisCycle,
                    cyclesRemaining = pace.CyclesRemaining,
                    onTrack = !pace.IsOverdue && !pace.IsFunded
                };
            })
            .ToList();

        var savingsByCycle = cycles
            .Select(cycle => new
            {
                key = ToCycleKey(cycle),
                result = CyclePositiveRewards(transactions, cycle, cycleDay)
            })
            .ToList();
        var activeCycleSavings = savingsByCycle
            .Where(item => item.result.HasActivity)
            .Select(item => item.result.Rewards)
            .ToList();
        var averageRewards = activeCycleSavings.Count == 0
            ? (decimal?)null
            : activeCycleSavings.Average();
        var futureFreeRewards = averageRewards.HasValue
            ? Math.Max(0m, averageRewards.Value - pool.RequiredPerCycleTotal)
            : (decimal?)null;

        return new
        {
            available = true,
            coverage = new
            {
                source = "SavingsGoalService and the Rewards ledger",
                currentCycle = pool.CurrentCycleKey,
                cycles = savingsByCycle.Select(item => item.key).ToList(),
                activeCyclesWithRewards = activeCycleSavings.Count,
                futureFreeRewardsPerCycle = futureFreeRewards.HasValue ? "exact" : "unavailable"
            },
            pool = new
            {
                rewardsBalance = pool.RewardsBalance,
                totalEarmarked = pool.TotalEarmarked,
                unassigned = pool.Unassigned,
                requiredPerCycleTotal = pool.RequiredPerCycleTotal,
                outstandingThisCycleTotal = pool.OutstandingThisCycleTotal
            },
            forecast = new
            {
                averageRewardsPerActiveCycle = averageRewards,
                requiredPerCycle = pool.RequiredPerCycleTotal,
                futureFreeRewardsPerCycle = futureFreeRewards,
                assumption = activeCycleSavings.Count == 0
                    ? "No completed Rewards cycle with activity was available; do not invent a forecast."
                    : "The average covers only the last requested cycles that had Rewards activity, then subtracts active savings-goal commitments."
            },
            goals = paceByGoal,
            completedGoalCount = goals.Count(goal => goal.Status == SavingsGoalStatus.Completed)
        };
    }

    private static string GoalPriorityLabel(string? priority) => priority switch
    {
        "High" => "high",
        "Low" => "low",
        _ => "medium"
    };

    private static string ToCycleKey(CycleKey cycle) =>
        $"{cycle.Year:D4}-{cycle.MonthIndex:D2}";
}