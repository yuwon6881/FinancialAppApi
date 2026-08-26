using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace FinancialAppApi.Services.SavingsGoals;

public partial class SavingsGoalService
{
    private sealed record FundingGoalState(int Id, decimal EarmarkedAmount, string? CycleFundedKey, decimal CycleFundedAmount);

    private static FundingGoalState FundingState(SavingsGoal goal) =>
        new(goal.Id, goal.EarmarkedAmount, goal.CycleFundedKey, goal.CycleFundedAmount);

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
        var previousStates = active.Select(FundingState).ToList();
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

        string? actionId = null;
        if (totalGranted > 0m)
        {
            actionId = Guid.NewGuid().ToString("N");
            _context.SavingsGoalFundingActions.Add(new SavingsGoalFundingAction
            {
                Id = actionId,
                UserId = _context.RequireCurrentUserId(),
                FundingBucket = fundingBucket,
                PreviousStateJson = JsonSerializer.Serialize(previousStates),
                ResultingStateJson = JsonSerializer.Serialize(active.Select(FundingState)),
                CreatedAt = DateTime.UtcNow
            });
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
            essentialsFreeToSpend,
            ActionId: actionId);
    }

    public async Task<SavingsGoalFundingResult> UndoCycleFundingAsync(
        string actionId,
        CancellationToken cancellationToken = default)
    {
        await using var poolLock = await _sharedPoolMutationLock.AcquireAsync(cancellationToken);
        var action = await _context.SavingsGoalFundingActions
            .FirstOrDefaultAsync(item => item.Id == actionId, cancellationToken);
        if (action == null)
            return new SavingsGoalFundingResult(SavingsGoalMutationStatus.NotFound, [], 0m, 0m, 0m, "Funding action was not found.");

        if (action.ReversedAt != null)
            return new SavingsGoalFundingResult(SavingsGoalMutationStatus.Success, await GetGoalsAsync(cancellationToken), 0m, 0m, 0m, ActionId: action.Id);

        var previous = JsonSerializer.Deserialize<List<FundingGoalState>>(action.PreviousStateJson) ?? [];
        var resulting = JsonSerializer.Deserialize<List<FundingGoalState>>(action.ResultingStateJson) ?? [];
        var ids = resulting.Select(state => state.Id).ToArray();
        var goals = await _context.SavingsGoals.Where(goal => ids.Contains(goal.Id)).ToListAsync(cancellationToken);
        var currentById = goals.ToDictionary(goal => goal.Id);
        var stale = resulting.Any(expected =>
            !currentById.TryGetValue(expected.Id, out var current)
            || current.EarmarkedAmount != expected.EarmarkedAmount
            || current.CycleFundedKey != expected.CycleFundedKey
            || current.CycleFundedAmount != expected.CycleFundedAmount);
        if (stale)
            return new SavingsGoalFundingResult(
                SavingsGoalMutationStatus.Conflict, [], 0m, 0m, 0m,
                "A commitment changed after cycle funding, so this funding action can no longer be undone safely.");

        foreach (var snapshot in previous)
        {
            if (!currentById.TryGetValue(snapshot.Id, out var goal)) continue;
            goal.EarmarkedAmount = snapshot.EarmarkedAmount;
            goal.CycleFundedKey = snapshot.CycleFundedKey;
            goal.CycleFundedAmount = snapshot.CycleFundedAmount;
        }
        action.ReversedAt = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new SavingsGoalFundingResult(
                SavingsGoalMutationStatus.Conflict, [], 0m, 0m, 0m,
                "A commitment changed while cycle funding was being undone. Refresh and try again.");
        }

        var cycleDay = await GetCycleDayAsync(cancellationToken);
        var free = await GetUnassignedAsync(action.FundingBucket, cycleDay, cancellationToken);
        return new SavingsGoalFundingResult(
            SavingsGoalMutationStatus.Success,
            await GetGoalsAsync(cancellationToken),
            0m,
            action.FundingBucket == SavingsGoalFundingBucket.Rewards ? free : 0m,
            action.FundingBucket == SavingsGoalFundingBucket.Essentials ? free : 0m,
            ActionId: action.Id);
    }
}
