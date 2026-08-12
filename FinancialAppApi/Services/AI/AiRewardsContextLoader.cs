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
        IReadOnlyList<AiWishlistRow> wishlistRows,
        object? wishlistForecast,
        bool sensitiveMode,
        CancellationToken cancellationToken)
    {
        if (!queryPlan.NeedsRewards) return null;

        var goals = await _savingsGoalService.GetGoalsAsync(cancellationToken);
        var rewardsPool = await _savingsGoalService.GetPoolSummaryAsync(SavingsGoalFundingBucket.Rewards, cancellationToken);
        var essentialsPool = await _savingsGoalService.GetPoolSummaryAsync(SavingsGoalFundingBucket.Essentials, cancellationToken);
        if (sensitiveMode)
        {
            return new
            {
                available = false,
                reason = "Commitment and reward amounts are hidden while sensitive mode is active.",
                currentCycle = rewardsPool.CurrentCycleKey,
                commitmentCount = goals.Count,
                rewardCount = wishlistRows.Count,
                historyRedacted = true
            };
        }

        var currentCycle = rewardsPool.CurrentCycleKey;
        var commitments = goals
            .Select(goal =>
            {
                if (goal.Status != SavingsGoalStatus.Active)
                {
                    return new
                    {
                        id = goal.Id,
                        name = goal.Name,
                        fundingBucket = goal.FundingBucket,
                        targetAmount = goal.TargetAmount,
                        earmarkedAmount = goal.EarmarkedAmount,
                        remaining = 0m,
                        targetDate = goal.TargetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        priority = GoalPriorityLabel(goal.Priority),
                        status = "completed",
                        recurring = goal.IsRecurring,
                        recurrenceMonths = goal.RecurrenceMonths,
                        pacePerCycle = 0m,
                        fundedThisCycle = 0m,
                        outstandingThisCycle = 0m,
                        cyclesRemaining = 0,
                        completedAt = goal.CompletedAt?.ToString("O", CultureInfo.InvariantCulture)
                    };
                }
                var pace = SavingsGoalPacing.ComputePace(
                    goal,
                    _financialClock.Today,
                    cycleDay,
                    currentCycle);
                var status = pace.IsFunded
                    ? "ready"
                    : pace.IsOverdue
                        ? "overdue"
                        : pace.OutstandingThisCycle > 0m ? "needs-funding" : "on-pace";
                return new
                {
                    id = goal.Id,
                    name = goal.Name,
                    fundingBucket = goal.FundingBucket,
                    targetAmount = goal.TargetAmount,
                    earmarkedAmount = goal.EarmarkedAmount,
                    remaining = pace.Remaining,
                    targetDate = goal.TargetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    priority = GoalPriorityLabel(goal.Priority),
                    status,
                    recurring = goal.IsRecurring,
                    recurrenceMonths = goal.RecurrenceMonths,
                    pacePerCycle = pace.RequiredPerCycle,
                    fundedThisCycle = pace.FundedThisCycle,
                    outstandingThisCycle = pace.OutstandingThisCycle,
                    cyclesRemaining = pace.CyclesRemaining,
                    completedAt = (string?)null
                };
            })
            .ToList();

        var rewards = wishlistRows.Select(item => new
        {
            id = item.Id,
            name = item.Name,
            price = item.Price,
            priority = GoalPriorityLabel(item.Priority),
            status = item.IsPurchased ? "claimed" : item.IsActive ? "focused" : "queued",
            claimable = !item.IsPurchased && rewardsPool.Unassigned >= item.Price,
            amountRemaining = item.IsPurchased ? 0m : Math.Max(0m, item.Price - rewardsPool.Unassigned),
            createdAt = item.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            claimedAt = item.PurchasedAt?.ToString("O", CultureInfo.InvariantCulture)
        }).ToList();

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
            ? Math.Max(0m, averageRewards.Value - rewardsPool.RequiredPerCycleTotal)
            : (decimal?)null;

        object PoolPayload(SavingsGoalPoolSummary pool)
        {
            var active = goals.Where(goal => goal.Status == SavingsGoalStatus.Active && goal.FundingBucket == pool.FundingBucket).ToList();
            var paces = active.Select(goal => SavingsGoalPacing.ComputePace(goal, _financialClock.Today, cycleDay, pool.CurrentCycleKey)).ToList();
            return new
            {
                fundingBucket = pool.FundingBucket,
                balance = pool.Balance,
                totalEarmarked = pool.TotalEarmarked,
                free = pool.Unassigned,
                requiredPerCycleTotal = pool.RequiredPerCycleTotal,
                fundedThisCycleTotal = paces.Sum(pace => pace.FundedThisCycle),
                outstandingThisCycleTotal = pool.OutstandingThisCycleTotal
            };
        }

        return new
        {
            available = true,
            coverage = new
            {
                source = "SavingsGoalService and the Rewards ledger",
                currentCycle = rewardsPool.CurrentCycleKey,
                cycles = savingsByCycle.Select(item => item.key).ToList(),
                activeCyclesWithRewards = activeCycleSavings.Count,
                futureFreeRewardsPerCycle = futureFreeRewards.HasValue ? "exact" : "unavailable"
            },
            pools = new { rewards = PoolPayload(rewardsPool), essentials = PoolPayload(essentialsPool) },
            forecast = new
            {
                averageRewardsPerActiveCycle = averageRewards,
                requiredPerCycle = rewardsPool.RequiredPerCycleTotal,
                futureFreeRewardsPerCycle = futureFreeRewards,
                assumption = activeCycleSavings.Count == 0
                    ? "No completed Rewards cycle with activity was available; do not invent a forecast."
                    : "The average covers only the last requested cycles that had Rewards activity, then subtracts active savings-goal commitments."
            },
            commitments,
            rewards,
            rewardForecast = wishlistForecast
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
