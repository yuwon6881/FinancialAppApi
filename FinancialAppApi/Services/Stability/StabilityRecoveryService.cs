using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Stability;

/// <summary>One contributing bucket's share of a proposed top-up. A ratio, so not obfuscated.</summary>
public sealed record StabilityRecoveryDrawDto(string Bucket, decimal Share);

/// <summary>
/// The emergency-fund recovery state the dashboard carries. Money fields are obfuscated strings,
/// matching every other money field in the dashboard payload.
/// <para>
/// <c>SuggestedDraws</c> carries shares rather than amounts on purpose: the amount depends on an
/// income figure the server does not know at dashboard time. The client turns the shares into
/// amounts as the user types, and the server recomputes them authoritatively on save.
/// </para>
/// </summary>
public sealed record StabilityRecoveryDto(
    bool IsActive,
    string HighWaterMark,
    string Target,
    string RecoverableCeiling,
    string CurrentBalance,
    string OutstandingShortfall,
    int CyclesRemaining,
    string RequiredThisCycle,
    string ToppedUpThisCycle,
    string OutstandingThisCycle,
    bool IsOverdue,
    string? LastDrawdownCycleKey,
    string LastDrawdownAmount,
    string EssentialsCommitted,
    string RewardsCommitted,
    IReadOnlyList<StabilityRecoveryDrawDto> SuggestedDraws);

/// <summary>The fund's live position, used by the income path to hold a split to the target.</summary>
public sealed record StabilityState(decimal CurrentBalance, decimal Target, int Year, int MonthIndex);

/// <summary>
/// Loads the state <see cref="StabilityRecoveryPlanner"/> needs and packages its output for the
/// dashboard. The EF half of the feature; every decision worth testing lives in the planner.
/// </summary>
public class StabilityRecoveryService
{
    private const string Stability = "Stability";

    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly FinancialClock _financialClock;

    public StabilityRecoveryService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        FinancialClock? financialClock = null)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _financialClock = financialClock ?? FinancialClock.Utc;
    }

    /// <summary>
    /// Builds the dashboard's recovery block for the cycle the caller is showing.
    /// <para>
    /// <c>essentialsCommitted</c> is this cycle's unpaid Essentials bills, which the caller already
    /// has. The caller also supplies unpaid recurring Rewards bills; the matching savings-goal
    /// figure -- what active goals still need -- is loaded here rather than threaded in. Both kinds
    /// of Rewards commitment are one floor, so refilling the buffer never spends money already
    /// promised somewhere else.
    /// </para>
    /// </summary>
    public async Task<StabilityRecoveryDto> BuildAsync(
        FinancialSetting setting,
        int year,
        int monthIndex,
        IReadOnlyList<Transaction> activeCycleTxs,
        decimal openingStability,
        decimal currentStability,
        decimal essentialsCommitted,
        CancellationToken cancellationToken = default,
        decimal rewardsRecurringCommitted = 0m)
    {
        // Must run before the MAX below. InvalidateFromAsync deletes cached rows on every
        // transaction mutation, so reading the high-water mark off a partially-invalidated table
        // under-reports it -- and an under-reported mark makes the card silently disappear, which
        // is the failure mode nobody would report as a bug.
        await _cycleBalanceService.EnsureComputedThroughAsync(year, monthIndex, setting.CycleDay, cancellationToken);

        var history = await _context.CycleBalances
            .AsNoTracking()
            .Where(balance => balance.Year < year || (balance.Year == year && balance.MonthIndex <= monthIndex))
            .OrderBy(balance => balance.Year)
            .ThenBy(balance => balance.MonthIndex)
            .Select(balance => new { balance.Year, balance.MonthIndex, balance.StabilityBalance })
            .ToListAsync(cancellationToken);

        var highWaterMark = 0m;
        string? lastDrawdownCycleKey = null;
        var lastDrawdownAmount = 0m;
        var previous = 0m;
        foreach (var row in history)
        {
            if (row.StabilityBalance > highWaterMark) highWaterMark = row.StabilityBalance;
            if (row.StabilityBalance < previous)
            {
                lastDrawdownCycleKey = StabilityRecoveryPlanner.CycleKey(row.Year, row.MonthIndex);
                lastDrawdownAmount = previous - row.StabilityBalance;
            }
            previous = row.StabilityBalance;
        }

        // The cycle being viewed has no settled row of its own yet, so its withdrawals are read
        // straight off the ledger with the same rule that produces the balance.
        var withdrawnThisCycle = activeCycleTxs.Sum(WithdrawalAmount);
        if (withdrawnThisCycle > 0m)
        {
            lastDrawdownCycleKey = StabilityRecoveryPlanner.CycleKey(year, monthIndex);
            lastDrawdownAmount = withdrawnThisCycle;
        }

        // The cycle's own peak, not just the balance it happens to end on. Cached rows record where
        // the fund stood at a boundary, so money that arrived and was spent inside a single cycle --
        // a bonus paid in and then dipped into -- looked like it had never been there, and the dip
        // registered as no drawdown at all.
        highWaterMark = Math.Max(highWaterMark, PeakWithinCycle(openingStability, activeCycleTxs));

        var currentCycleKey = StabilityRecoveryPlanner.CycleKey(year, monthIndex);
        var drawdown = StabilityRecoveryPlanner.ComputeDrawdown(
            highWaterMark,
            setting.TargetStabilityFund,
            currentStability,
            lastDrawdownCycleKey,
            lastDrawdownAmount);

        var incomeThisCycle = activeCycleTxs
            .Where(IsIncome)
            .Sum(transaction => transaction.Amount);
        var stabilityCredit = activeCycleTxs
            .Sum(transaction => Math.Max(0m, CategoryAttributionService.GetCategoryAmount(transaction, Stability)));
        var toppedUp = StabilityRecoveryPlanner.ToppedUpThisCycle(
            stabilityCredit, incomeThisCycle, setting.StabilityAlloc);

        var cyclesRemaining = StabilityRecoveryPlanner.CyclesRemaining(
            lastDrawdownCycleKey, currentCycleKey, FinancialConstants.StabilityRecoveryCycles);
        var pace = StabilityRecoveryPlanner.ComputePace(drawdown, cyclesRemaining, toppedUp);

        var rewardsCommitted = rewardsRecurringCommitted + await GetGoalFundingDueAsync(setting.CycleDay, cancellationToken);

        return new StabilityRecoveryDto(
            drawdown.IsActive,
            ObfuscationHelper.Obfuscate(drawdown.HighWaterMark),
            ObfuscationHelper.Obfuscate(drawdown.Target),
            ObfuscationHelper.Obfuscate(drawdown.RecoverableCeiling),
            ObfuscationHelper.Obfuscate(drawdown.CurrentBalance),
            ObfuscationHelper.Obfuscate(drawdown.OutstandingShortfall),
            pace.CyclesRemaining,
            ObfuscationHelper.Obfuscate(pace.RequiredThisCycle),
            ObfuscationHelper.Obfuscate(pace.ToppedUpThisCycle),
            ObfuscationHelper.Obfuscate(pace.OutstandingThisCycle),
            pace.IsOverdue,
            drawdown.LastDrawdownCycleKey,
            ObfuscationHelper.Obfuscate(drawdown.LastDrawdownAmount),
            ObfuscationHelper.Obfuscate(essentialsCommitted),
            ObfuscationHelper.Obfuscate(rewardsCommitted),
            BuildSuggestedDraws(setting));
    }

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
        string? excludeTransactionId = null,
        CancellationToken cancellationToken = default)
    {
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            _financialClock.Today, setting.CycleDay);

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

        return new StabilityState(openingStability + net, setting.TargetStabilityFund, year, monthIndex);
    }

    /// <summary>
    /// What active savings goals still need this cycle. Goals are earmarks against Rewards, so a
    /// proportional top-up that ignored them would have two features quietly claiming the same
    /// money -- and the deadline-bound one would lose. Uses the goals' own pacing rather than a
    /// second implementation of it.
    /// </summary>
    private async Task<decimal> GetGoalFundingDueAsync(int cycleDay, CancellationToken cancellationToken)
    {
        var active = await _context.SavingsGoals
            .AsNoTracking()
            .Where(goal => goal.Status == SavingsGoalStatus.Active)
            .ToListAsync(cancellationToken);
        if (active.Count == 0) return 0m;

        var today = _financialClock.Today;
        var (goalYear, goalMonth) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
        var cycleKey = StabilityRecoveryPlanner.CycleKey(goalYear, goalMonth);

        return active.Sum(goal =>
            SavingsGoals.SavingsGoalPacing.ComputePace(goal, today, cycleDay, cycleKey).OutstandingThisCycle);
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

    /// <summary>
    /// The highest the fund reached at any point during the cycle, replaying its transactions in
    /// posting order from the opening balance. Ordered by date then <c>PostedAt</c> then id so the
    /// walk is deterministic — two transactions on the same day must not swap places between
    /// requests and move the peak.
    /// </summary>
    private static decimal PeakWithinCycle(decimal openingBalance, IReadOnlyList<Transaction> cycleTxs)
    {
        var running = openingBalance;
        var peak = openingBalance;
        foreach (var transaction in cycleTxs
                     .OrderBy(t => t.Date)
                     .ThenBy(t => t.PostedAt)
                     .ThenBy(t => t.Id, StringComparer.Ordinal))
        {
            running += CategoryAttributionService.GetCategoryAmount(transaction, Stability);
            if (running > peak) peak = running;
        }
        return peak;
    }

    /// <summary>
    /// Money that left the fund. One rule covers all three shapes a withdrawal takes -- a negative
    /// Stability row, a <c>Transfer:Stability-&gt;X</c>, and a downward Adjustment (which is one of
    /// those two wearing a different Category; the attribution rule never reads Category). Using the
    /// same function that produces the on-screen balance is the point: a bespoke classifier would
    /// eventually disagree with it and ask for money back that the balance says is still there.
    /// </summary>
    private static decimal WithdrawalAmount(Transaction transaction)
    {
        return Math.Max(0m, -CategoryAttributionService.GetCategoryAmount(transaction, Stability));
    }

    private static bool IsIncome(Transaction transaction)
    {
        return string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase)
            || transaction.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase);
    }
}
