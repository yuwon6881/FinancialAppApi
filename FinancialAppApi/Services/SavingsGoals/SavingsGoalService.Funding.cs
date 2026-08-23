using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.SavingsGoals;

public partial class SavingsGoalService
{
    /// <summary>
    /// Pours the selected bucket's currently-unassigned money through the goal waterfall, giving each goal only
    /// what it still needs this cycle.
    ///
    /// Precise rather than once-per-cycle: a goal the user already topped up by hand is outstanding
    /// zero and receives nothing, releasing money from a goal makes exactly that goal fundable again,
    /// and tapping twice with nothing outstanding is a no-op.
    /// </summary>
    public async Task<SavingsGoalFundingResult> FundCurrentCycleAsync(
        string fundingBucket = SavingsGoalFundingBucket.Rewards,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowedFundingBucket(fundingBucket))
        {
            return new SavingsGoalFundingResult(
                SavingsGoalMutationStatus.FundingBucketInvalid,
                [],
                0m,
                0m,
                0m,
                "Commitments can only use the Essentials or Rewards pool.");
        }

        await using var poolLock = await _sharedPoolMutationLock.AcquireAsync(cancellationToken);
        var cycleDay = await GetCycleDayAsync(cancellationToken);
        var today = _financialClock.Today;
        var cycleKey = CurrentCycleKey(today, cycleDay);

        var available = await GetUnassignedAsync(fundingBucket, cycleDay, cancellationToken);
        var active = await _context.SavingsGoals
            .Where(goal => goal.Status == SavingsGoalStatus.Active && goal.FundingBucket == fundingBucket)
            .ToListAsync(cancellationToken);

        var byId = active.ToDictionary(goal => goal.Id);
        var totalGranted = 0m;
        var waterfall = SavingsGoalPacing.Distribute(active, available, today, cycleDay, cycleKey);
        totalGranted = waterfall.TotalGranted;
        foreach (var grant in waterfall.Grants)
        {
            if (grant.Amount <= 0m) continue;
            var goal = byId[grant.GoalId];
            var next = Math.Min(goal.TargetAmount, goal.EarmarkedAmount + grant.Amount);
            ApplyCycleFunding(goal, next - goal.EarmarkedAmount, cycleKey);
            InvalidateCompletionUndo(goal);
            goal.EarmarkedAmount = next;
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
                0m,
                0m,
                "A commitment changed while this cycle was being funded. Refresh and try again.");
        }

        var rewardsFreeToSpend = fundingBucket == SavingsGoalFundingBucket.Rewards
            ? waterfall.FreeToSpend
            : 0m;
        var essentialsFreeToSpend = fundingBucket == SavingsGoalFundingBucket.Essentials
            ? waterfall.FreeToSpend
            : 0m;

        return new SavingsGoalFundingResult(
            SavingsGoalMutationStatus.Success,
            await GetGoalsAsync(cancellationToken),
            totalGranted,
            rewardsFreeToSpend,
            essentialsFreeToSpend);
    }
}
