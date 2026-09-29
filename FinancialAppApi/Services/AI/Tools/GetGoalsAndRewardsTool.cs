using System.Globalization;
using System.Text.Json.Nodes;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.SavingsGoals;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.AI.Tools;

// Savings goals ("commitments") and Rewards wishlist items. Pools, earmarks and pace come from
// SavingsGoalService; the wishlist timeline is WishlistForecaster, averaged over the same three
// cycles before the current one that the Wishlist page uses, so both quote the same date.
public sealed class GetGoalsAndRewardsTool : IAiTool
{
    private const int ForecastCycles = 3;

    private readonly AppDbContext _context;
    private readonly SavingsGoalService _goals;
    private readonly AiTransactionQueryService _transactions;

    public GetGoalsAndRewardsTool(AppDbContext context, SavingsGoalService goals, AiTransactionQueryService transactions)
    {
        _context = context;
        _goals = goals;
        _transactions = transactions;
    }

    public string Name => "get_goals_and_rewards";

    public string Description =>
        "The user's savings goals (commitments funded from Essentials or Rewards) with target, earmarked amount, " +
        "remaining, deadline, and pace status; the Rewards and Essentials pools (balance, earmarked, free); and Rewards " +
        "wishlist items with price, whether they can be claimed now, and the estimated date they become affordable. " +
        "Use for goals, 'can I afford X from Rewards', and 'when can I get X'. Name rewardName to forecast one item.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("rewardName", AiToolSchema.String("Part of one wishlist item's name to forecast, e.g. \"camera\"."))
    ]);

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public IReadOnlySet<string> AmountProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "targetAmount", "earmarkedAmount", "pacePerCycle", "fundedThisCycle", "outstandingThisCycle",
        "totalEarmarked", "free", "requiredPerCycleTotal", "outstandingThisCycleTotal", "amountRemaining",
        "claimable", "forecast"
    };

    public string ProgressLabel(AiToolArgs args) => "Checking your goals and Rewards";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var rewardName = args.OptionalString("rewardName", 120);
        var goals = await _goals.GetGoalsAsync(cancellationToken);
        var rewardsPool = await _goals.GetPoolSummaryAsync(SavingsGoalFundingBucket.Rewards, cancellationToken);
        var essentialsPool = await _goals.GetPoolSummaryAsync(SavingsGoalFundingBucket.Essentials, cancellationToken);
        var wishlist = await _context.WishlistItems
            .AsNoTracking()
            .OrderBy(item => item.IsPurchased)
            .ThenByDescending(item => item.IsActive)
            .ThenByDescending(item => item.CreatedAt)
            .Take(100)
            .Select(item => new AiAssistantService.AiWishlistRow(
                item.Id, item.Name, item.Price, item.Priority, item.IsActive, item.IsPurchased, item.CreatedAt, item.PurchasedAt))
            .ToListAsync(cancellationToken);

        context.Evidence.RecordAll(AiEvidenceLedger.SavingsGoal, goals.Select(goal => goal.Id.ToString(CultureInfo.InvariantCulture)));
        context.Evidence.RecordAll(AiEvidenceLedger.Wishlist, wishlist.Select(item => item.Id.ToString(CultureInfo.InvariantCulture)));

        return AiToolResult.Of(new
        {
            pools = new[] { Pool(rewardsPool, goals, context), Pool(essentialsPool, goals, context) },
            goals = goals.Select(goal => Goal(goal, rewardsPool.CurrentCycleKey, context)).ToList(),
            rewards = wishlist.Select(item => new
            {
                id = item.Id,
                name = item.Name,
                price = item.Price,
                priority = PriorityLabel(item.Priority),
                status = item.IsPurchased ? "claimed" : item.IsActive ? "focused" : "queued",
                claimable = !item.IsPurchased && rewardsPool.Unassigned >= item.Price,
                amountRemaining = item.IsPurchased ? 0m : Math.Max(0m, item.Price - rewardsPool.Unassigned),
                claimedAt = item.PurchasedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            }).ToList(),
            // A timeline is a function of balances; it is not offered while they are hidden.
            forecast = context.SensitiveMode || wishlist.All(item => item.IsPurchased)
                ? null
                : await ForecastAsync(wishlist, rewardName, rewardsPool, context, cancellationToken)
        });
    }

    private async Task<object?> ForecastAsync(
        IReadOnlyList<AiAssistantService.AiWishlistRow> wishlist,
        string? rewardName,
        SavingsGoalPoolSummary rewardsPool,
        AiToolContext context,
        CancellationToken cancellationToken)
    {
        var current = AiCycleResolver.Current(context.Today, context.CycleDay);
        var cycles = Enumerable.Range(1, ForecastCycles).Select(offset => current.AddCycles(-offset)).ToList();
        var (rows, _) = await _transactions.LoadAsync(
            _transactions.Scoped(new AiTransactionFilter(AiCycleResolver.MergedRanges(cycles, context.CycleDay))),
            GetCycleSummaryTool.RowCap * ForecastCycles,
            cancellationToken);

        var results = AiAssistantService.ComputeWishlistForecast(new AiAssistantService.WishlistForecastPolicy(
            wishlist,
            rows.Select(row => new AiAssistantService.AiTransactionRow(
                row.Id, TransactionDate.StartOfDate(row.Date), row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.Description, row.Category, row.LedgerCategory, row.Amount, row.PostedAt)).ToList(),
            cycles.Select(cycle => new AiAssistantService.CycleKey(cycle.Year, cycle.MonthIndex)).ToList(),
            context.CycleDay,
            AiCycleResolver.Range(current, context.CycleDay).Start,
            context.Today.ToDateTime(TimeOnly.MinValue),
            rewardName,
            rewardsPool.Unassigned,
            rewardsPool.RequiredPerCycleTotal));

        return results.Select(result => new
        {
            id = result.WishlistItemId,
            name = result.Name,
            status = result.Status switch
            {
                AiAssistantService.WishlistForecastStatus.Estimated => "estimated",
                AiAssistantService.WishlistForecastStatus.AlreadyReached => "already-affordable",
                AiAssistantService.WishlistForecastStatus.NotReachable => "not-currently-reachable",
                AiAssistantService.WishlistForecastStatus.InsufficientData => "insufficient-history",
                _ => "ambiguous-name"
            },
            remaining = result.RemainingAmount,
            savingsPerCycle = result.TypicalSavingsPerCycle,
            estimatedCycles = result.EstimatedCycles,
            estimatedDate = result.EstimatedDate,
            assumption = result.Assumption,
            candidates = result.Candidates
        }).ToList();
    }

    private static object Pool(SavingsGoalPoolSummary pool, IReadOnlyList<SavingsGoal> goals, AiToolContext context) => new
    {
        fundingBucket = pool.FundingBucket,
        balance = pool.Balance,
        totalEarmarked = pool.TotalEarmarked,
        free = pool.Unassigned,
        requiredPerCycleTotal = pool.RequiredPerCycleTotal,
        outstandingThisCycleTotal = pool.OutstandingThisCycleTotal,
        activeGoals = goals.Count(goal => goal.Status == SavingsGoalStatus.Active && goal.FundingBucket == pool.FundingBucket)
    };

    private static object Goal(SavingsGoal goal, string currentCycleKey, AiToolContext context)
    {
        var active = goal.Status == SavingsGoalStatus.Active;
        var pace = active ? SavingsGoalPacing.ComputePace(goal, context.Today, context.CycleDay, currentCycleKey) : null;
        return new
        {
            id = goal.Id,
            name = goal.Name,
            fundingBucket = goal.FundingBucket,
            targetAmount = goal.TargetAmount,
            earmarkedAmount = goal.EarmarkedAmount,
            remaining = pace?.Remaining ?? 0m,
            targetDate = goal.TargetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            priority = PriorityLabel(goal.Priority),
            status = pace == null ? "completed"
                : pace.IsFunded ? "ready"
                : pace.IsOverdue ? "overdue"
                : pace.OutstandingThisCycle > 0m ? "needs-funding" : "on-pace",
            recurring = goal.IsRecurring,
            recurrenceMonths = goal.RecurrenceMonths,
            pacePerCycle = pace?.RequiredPerCycle,
            fundedThisCycle = pace?.FundedThisCycle,
            outstandingThisCycle = pace?.OutstandingThisCycle,
            cyclesRemaining = pace?.CyclesRemaining,
            completedAt = goal.CompletedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
    }

    private static string PriorityLabel(string? priority) => priority switch
    {
        "High" => "high",
        "Low" => "low",
        _ => "medium"
    };
}
