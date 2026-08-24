using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Stability;

public partial class StabilityRecoveryService
{
    /// <summary>
    /// The authoritative live fund balance for the income path.
    /// <para>
    /// <c>excludeTransactionId</c> drops that transaction and the <c>-split-*</c> children it
    /// generated, so re-saving an existing salary is measured against the fund without its own
    /// previous contribution rather than on top of it.
    /// </para>
    /// </summary>
    public async Task<StabilityState> GetStabilityStateAsync(
        FinancialSetting setting,
        DateOnly transactionDate,
        string? excludeTransactionId = null,
        CancellationToken cancellationToken = default)
    {
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            transactionDate, setting.CycleDay);
        var planRevisions = await _planRevisionService.GetAsync(setting, cancellationToken);

        await _cycleBalanceService.EnsureComputedThroughAsync(
            year, monthIndex, setting.CycleDay, cancellationToken);

        var (_, _, openingStability, _) = await _cycleBalanceService.GetOpeningBalanceAsync(
            year, monthIndex, setting.CycleDay, cancellationToken);

        var (start, end, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, setting.CycleDay);
        var startDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(start));
        var endExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(end));

        var query = _context.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.Date >= startDate && transaction.Date < endExclusive);

        if (!string.IsNullOrEmpty(excludeTransactionId))
        {
            var childPrefix = $"{excludeTransactionId}-split-";
            query = query.Where(transaction =>
                transaction.Id != excludeTransactionId && !transaction.Id.StartsWith(childPrefix));
        }

        var cycleTxs = await query.ToListAsync(cancellationToken);
        var net = cycleTxs.Sum(transaction => CategoryAttributionService.GetCategoryAmount(transaction, Stability));

        var currentBalance = openingStability + net;
        var previous = await _context.CycleBalances
            .AsNoTracking()
            .Where(balance => balance.Year < year || (balance.Year == year && balance.MonthIndex < monthIndex))
            .OrderByDescending(balance => balance.Year)
            .ThenByDescending(balance => balance.MonthIndex)
            .Select(balance => new
            {
                balance.StabilityReloadOutstanding,
                balance.StabilityReloadOldestDate,
                balance.StabilityReloadObligations
            })
            .FirstOrDefaultAsync(cancellationToken);
        var openingReload = previous == null
            ? new ReloadState(0m, null, 0m, 0m)
            : StabilityReloadObligationCache.OpeningState(
                previous.StabilityReloadOutstanding,
                previous.StabilityReloadOldestDate,
                previous.StabilityReloadObligations);
        var cycleStartUtc = DateTime.SpecifyKind(start, DateTimeKind.Utc);
        var cycleEndExclusiveUtc = DateTime.SpecifyKind(end.Date.AddDays(1), DateTimeKind.Utc);
        var planAtStart = StabilityPlanRevisionService.At(planRevisions, cycleStartUtc);
        var cyclePlanPoints = planRevisions
            .Where(revision => revision.EffectiveAt > cycleStartUtc && revision.EffectiveAt < cycleEndExclusiveUtc)
            .Select(revision => new ReloadPlanPoint(revision.EffectiveAt, revision.TargetStabilityFund))
            .Prepend(new ReloadPlanPoint(planAtStart.EffectiveAt, planAtStart.TargetStabilityFund))
            .ToList();
        var replay = StabilityReloadLedger.Replay(
            openingReload,
            openingStability,
            cyclePlanPoints,
            StabilityReloadLedger.DescribeAll(
                cycleTxs,
                transaction => StabilityPlanRevisionService.At(
                    planRevisions,
                    transaction.PostedAt).StabilityAlloc));

        return new StabilityState(
            currentBalance,
            StabilityPlanRevisionService.At(
                planRevisions,
                transactionDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc)).TargetStabilityFund,
            replay.Outstanding,
            year,
            monthIndex);
    }

    public Task<StabilityState> GetStabilityStateAsync(
        FinancialSetting setting,
        string? excludeTransactionId = null,
        CancellationToken cancellationToken = default) =>
        GetStabilityStateAsync(setting, _financialClock.Today, excludeTransactionId, cancellationToken);

    /// <summary>
    /// What active savings goals already earmark plus still need this cycle. Goals claim their
    /// eligible bucket, so a proportional top-up that ignored them would have two features quietly
    /// claiming the same money -- and the deadline-bound one would lose. Uses the goals' own pacing rather than a
    /// second implementation of it.
    /// <para>
    /// Answers zero for any cycle but the current one. A goal's pace is measured from today's
    /// deadline distance and its tally is keyed to the live cycle, so asking it about a cycle that
    /// has already closed reports a commitment that cannot still be owed. Only the current cycle
    /// can offer a top-up in the first place, so the figure has no reader there anyway.
    /// </para>
    /// </summary>
    private async Task<GoalCommitments> GetGoalCommitmentsAsync(
        int year,
        int monthIndex,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        var (currentYear, currentMonth) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            _financialClock.Today, cycleDay);
        if (year != currentYear || monthIndex != currentMonth) return new GoalCommitments(0m, 0m);

        var active = await _context.SavingsGoals
            .AsNoTracking()
            .Where(goal => goal.Status == SavingsGoalStatus.Active)
            .ToListAsync(cancellationToken);
        if (active.Count == 0) return new GoalCommitments(0m, 0m);

        var cycleKey = StabilityRecoveryPlanner.CycleKey(currentYear, currentMonth);

        var commitments = active
            .Select(goal => new
            {
                Bucket = goal.FundingBucket,
                Amount = goal.EarmarkedAmount + SavingsGoals.SavingsGoalPacing
                    .ComputePace(goal, _financialClock.Today, cycleDay, cycleKey).OutstandingThisCycle
            })
            .ToList();
        return new GoalCommitments(
            commitments.Where(item => item.Bucket == SavingsGoalFundingBucket.Essentials).Sum(item => item.Amount),
            commitments.Where(item => item.Bucket == SavingsGoalFundingBucket.Rewards).Sum(item => item.Amount));
    }

    /// <summary>
    /// The proportional split of a top-up across the three contributing buckets. Excludes any
    /// bucket set to 0%, which cannot give up a share it never receives.
    /// </summary>
    private static IReadOnlyList<StabilityRecoveryDrawDto> BuildSuggestedDraws(FinancialSetting setting)
    {
        var buckets = new[]
        {
            ("Essentials", setting.EssentialsAlloc),
            ("Growth", setting.GrowthAlloc),
            ("Rewards", setting.RewardsAlloc)
        }.Where(bucket => bucket.Item2 > 0m).ToList();

        var total = buckets.Sum(bucket => bucket.Item2);
        if (total <= 0m) return Array.Empty<StabilityRecoveryDrawDto>();

        // Rounded, with the drift pushed onto the largest share, so the three sum to exactly 1.
        // An allocation like 0.50/0.85 is a repeating decimal, and the client multiplies these by
        // an income amount -- unrounded, the parts would not add up to the whole it was given.
        var draws = buckets
            .Select(bucket => new StabilityRecoveryDrawDto(
                bucket.Item1,
                Math.Round(bucket.Item2 / total, 6, MidpointRounding.AwayFromZero)))
            .ToList();

        var largest = 0;
        for (var i = 1; i < draws.Count; i++)
        {
            if (draws[i].Share > draws[largest].Share) largest = i;
        }
        draws[largest] = draws[largest] with { Share = draws[largest].Share + (1m - draws.Sum(draw => draw.Share)) };
        return draws;
    }

    private static bool IsIncome(Transaction transaction)
    {
        return string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase)
            || transaction.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase);
    }
}
