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
    string MarkedTotal,
    string Target,
    string CurrentBalance,
    string OutstandingShortfall,
    string OpeningOutstanding,
    string? OpeningOldestDate,
    int CyclesRemaining,
    string RequiredThisCycle,
    string ToppedUpThisCycle,
    string OutstandingThisCycle,
    bool IsOverdue,
    string? LastDrawdownCycleKey,
    string RepaidTotal,
    string EssentialsCommitted,
    string RewardsCommitted,
    IReadOnlyList<StabilityRecoveryDrawDto> SuggestedDraws,
    // RecoveryFromDate is the exact date of the oldest still-outstanding marked drawdown, or null
    // when the obligation has been cleared.
    //
    // A date rather than an amount because the client's only use for it is a ledger date filter:
    // the shortfall is a difference between two balances, and the transactions that produced it are
    // every Stability-attributed row since the fund was last full. Cycle-boundary granularity is
    // deliberate -- an intra-cycle attainment cannot be expressed as a ledger filter at all, and
    // opening the window early can only include movement the user genuinely made, never exclude
    // movement they did not.
    string? RecoveryFromDate);

/// <summary>The fund's live position, used by the income path to hold a split to the target.</summary>
public sealed record StabilityState(
    decimal CurrentBalance,
    decimal Target,
    decimal OutstandingObligation,
    int Year,
    int MonthIndex);

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

    private async Task BackfillCurrentCycleRecoveryIntentAsync(
        FinancialSetting setting,
        int year,
        int monthIndex,
        IReadOnlyList<Transaction> cycleTxs,
        CancellationToken cancellationToken)
    {
        var current = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            _financialClock.Today, setting.CycleDay);
        if (current != (year, monthIndex)) return;

        var changed = false;
        foreach (var parent in cycleTxs.Where(transaction =>
                     transaction.StabilityRecoveryTopUpAmount == null
                     && transaction.Amount > 0m
                     && IsIncome(transaction)
                     && !transaction.Id.Contains("-split-", StringComparison.Ordinal)))
        {
            var childPrefix = parent.Id + "-split-";
            var stabilityCredit = cycleTxs
                .Where(candidate => candidate.Id.StartsWith(childPrefix, StringComparison.Ordinal))
                .Sum(candidate => Math.Max(0m,
                    CategoryAttributionService.GetCategoryAmount(candidate, Stability)));
            var inferred = Math.Round(
                Math.Max(0m, stabilityCredit - parent.Amount * setting.StabilityAlloc),
                2,
                MidpointRounding.AwayFromZero);
            if (inferred <= 0m) continue;

            var stored = await _context.Transactions.FirstOrDefaultAsync(
                transaction => transaction.Id == parent.Id
                    && transaction.StabilityRecoveryTopUpAmount == null,
                cancellationToken);
            if (stored == null) continue;
            stored.StabilityRecoveryTopUpAmount = inferred;
            changed = true;
        }
        if (changed)
        {
            await _context.SaveChangesAsync(cancellationToken);
            // The inferred value changes replay, so a cache built before this compatibility
            // repair must not carry the old obligation into later cycles.
            await _cycleBalanceService.InvalidateFromAsync(year, monthIndex, cancellationToken);
        }
    }

    /// <summary>
    /// Builds the dashboard's recovery block for the cycle the caller is showing.
    /// <para>
    /// <c>essentialsCommitted</c> is this cycle's unpaid Essentials bills, which the caller already
    /// has. The caller also supplies unpaid recurring Rewards bills; the matching savings-goal
    /// figure -- active earmarks plus what goals still need this cycle -- is loaded here rather than threaded in. Both kinds
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
        await BackfillCurrentCycleRecoveryIntentAsync(
            setting, year, monthIndex, activeCycleTxs, cancellationToken);

        // Rebuild the cache before reading the prior reload state. InvalidateFromAsync deletes
        // cached rows on every transaction mutation, so replaying from a partially-invalidated
        // table would silently forget an older obligation.
        await _cycleBalanceService.EnsureComputedThroughAsync(year, monthIndex, setting.CycleDay, cancellationToken);

        var history = await _context.CycleBalances
            .AsNoTracking()
            .Where(balance => balance.Year < year || (balance.Year == year && balance.MonthIndex <= monthIndex))
            .OrderBy(balance => balance.Year)
            .ThenBy(balance => balance.MonthIndex)
            .Select(balance => new
            {
                balance.Year,
                balance.MonthIndex,
                balance.StabilityReloadOutstanding,
                balance.StabilityReloadMarkedAmount,
                balance.StabilityReloadOldestDate
            })
            .ToListAsync(cancellationToken);

        var previous = history
            .Where(row => row.Year < year || (row.Year == year && row.MonthIndex < monthIndex))
            .LastOrDefault();
        var openingReload = previous == null
            ? new ReloadState(0m, null, 0m, 0m)
            : new ReloadState(
                previous.StabilityReloadOutstanding,
                previous.StabilityReloadOldestDate,
                0m,
                0m);
        var replay = StabilityReloadLedger.Replay(
            openingReload,
            openingStability,
            setting.TargetStabilityFund,
            StabilityReloadLedger.DescribeAll(activeCycleTxs, setting.StabilityAlloc));

        var lastMarked = history.LastOrDefault(row => row.StabilityReloadMarkedAmount > 0m);
        var lastDrawdownCycleKey = replay.MarkedThisRun > 0m
            ? StabilityRecoveryPlanner.CycleKey(year, monthIndex)
            : lastMarked == null
                ? null
                : StabilityRecoveryPlanner.CycleKey(lastMarked.Year, lastMarked.MonthIndex);
        var currentCycleKey = StabilityRecoveryPlanner.CycleKey(year, monthIndex);
        var cyclesRemaining = StabilityRecoveryPlanner.CyclesRemaining(
            lastDrawdownCycleKey, currentCycleKey, FinancialConstants.StabilityRecoveryCycles);
        var pace = StabilityRecoveryPlanner.ComputePace(
            replay.Outstanding,
            cyclesRemaining,
            replay.RepaidThisRun);

        // The reporting window is the earlier of what is still outstanding and what this run marked.
        // FIFO repayment retires the oldest entries first, so a drawdown fully paid off this cycle
        // leaves the queue: anchoring only on the outstanding head dropped that row from the window
        // and understated both totals by it. Reading 801.77 marked and 450 back beside a 520 that
        // had visibly gone in is the same 70 missing twice.
        var windowStart = replay.OldestMarkedThisRunDate.HasValue
            && (!replay.OldestOutstandingDate.HasValue
                || replay.OldestMarkedThisRunDate.Value < replay.OldestOutstandingDate.Value)
            ? replay.OldestMarkedThisRunDate
            : replay.OldestOutstandingDate;

        var reloadTotals = replay.Outstanding > 0m && windowStart.HasValue
            ? await GetReloadTotalsAsync(
                windowStart.Value,
                year,
                monthIndex,
                setting.CycleDay,
                setting.StabilityAlloc,
                replay.Outstanding,
                cancellationToken)
            : (MarkedTotal: 0m, RepaidTotal: 0m);

        var rewardsCommitted = rewardsRecurringCommitted +
            await GetGoalCommitmentsAsync(year, monthIndex, setting.CycleDay, cancellationToken);

        return new StabilityRecoveryDto(
            replay.Outstanding > 0m,
            ObfuscationHelper.Obfuscate(reloadTotals.MarkedTotal),
            ObfuscationHelper.Obfuscate(setting.TargetStabilityFund),
            ObfuscationHelper.Obfuscate(currentStability),
            ObfuscationHelper.Obfuscate(replay.Outstanding),
            ObfuscationHelper.Obfuscate(openingReload.Outstanding),
            openingReload.OldestOutstandingDate?.ToString("yyyy-MM-dd"),
            pace.CyclesRemaining,
            ObfuscationHelper.Obfuscate(pace.RequiredThisCycle),
            ObfuscationHelper.Obfuscate(pace.ToppedUpThisCycle),
            ObfuscationHelper.Obfuscate(pace.OutstandingThisCycle),
            pace.IsOverdue,
            lastDrawdownCycleKey,
            ObfuscationHelper.Obfuscate(reloadTotals.RepaidTotal),
            ObfuscationHelper.Obfuscate(essentialsCommitted),
            ObfuscationHelper.Obfuscate(rewardsCommitted),
            BuildSuggestedDraws(setting),
            // The same window the totals were measured over, so "see every movement since then"
            // lands on exactly the rows those figures came from.
            windowStart?.ToString("yyyy-MM-dd"));
    }

    private async Task<(decimal MarkedTotal, decimal RepaidTotal)> GetReloadTotalsAsync(
        DateOnly oldestOutstandingDate,
        int year,
        int monthIndex,
        int cycleDay,
        decimal stabilityAlloc,
        decimal outstanding,
        CancellationToken cancellationToken)
    {
        var (_, cycleEnd, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        var start = TransactionDate.StartOfDate(oldestOutstandingDate);
        var endExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(cycleEnd));
        var rows = await _context.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.Date >= start && transaction.Date < endExclusive)
            // DescribeAll needs the salary parent to pair with its generated Stability child, so
            // Income is included alongside the three GetCategoryAmount shapes. The projection is
            // intentional: the dashboard only needs the ordering and reload-attribution fields.
            .Where(transaction => transaction.LedgerCategory.ToUpper().StartsWith("STABILITY")
                                  || transaction.LedgerCategory.ToUpper().StartsWith("INCOMESPLIT:")
                                  || transaction.LedgerCategory.ToUpper().StartsWith("TRANSFER:")
                                  || transaction.LedgerCategory.ToUpper() == "INCOME")
            .Select(transaction => new
            {
                transaction.Id,
                transaction.Date,
                transaction.PostedAt,
                transaction.Amount,
                transaction.LedgerCategory,
                transaction.StabilityRecoveryTopUpAmount,
                transaction.StabilityReloadIntent
            })
            .ToListAsync(cancellationToken);
        var markedTotal = StabilityReloadLedger.DescribeAll(
                rows.Select(row => new Transaction
                {
                    Id = row.Id,
                    Date = row.Date,
                    PostedAt = row.PostedAt,
                    Amount = row.Amount,
                    LedgerCategory = row.LedgerCategory,
                    StabilityRecoveryTopUpAmount = row.StabilityRecoveryTopUpAmount,
                    StabilityReloadIntent = row.StabilityReloadIntent
                }),
                stabilityAlloc)
            .Where(movement => movement.Marked)
            .Sum(movement => Math.Max(0m, -movement.Change));
        return (markedTotal, Math.Max(0m, markedTotal - outstanding));
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
        DateOnly transactionDate,
        string? excludeTransactionId = null,
        CancellationToken cancellationToken = default)
    {
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            transactionDate, setting.CycleDay);

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
                balance.StabilityReloadOldestDate
            })
            .FirstOrDefaultAsync(cancellationToken);
        var openingReload = previous == null
            ? new ReloadState(0m, null, 0m, 0m)
            : new ReloadState(
                previous.StabilityReloadOutstanding,
                previous.StabilityReloadOldestDate,
                0m,
                0m);
        var replay = StabilityReloadLedger.Replay(
            openingReload,
            openingStability,
            setting.TargetStabilityFund,
            StabilityReloadLedger.DescribeAll(cycleTxs, setting.StabilityAlloc));

        return new StabilityState(
            currentBalance,
            setting.TargetStabilityFund,
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
    /// What active savings goals already earmark plus still need this cycle. Goals claim Rewards, so a
    /// proportional top-up that ignored them would have two features quietly claiming the same
    /// money -- and the deadline-bound one would lose. Uses the goals' own pacing rather than a
    /// second implementation of it.
    /// <para>
    /// Answers zero for any cycle but the current one. A goal's pace is measured from today's
    /// deadline distance and its tally is keyed to the live cycle, so asking it about a cycle that
    /// has already closed reports a commitment that cannot still be owed. Only the current cycle
    /// can offer a top-up in the first place, so the figure has no reader there anyway.
    /// </para>
    /// </summary>
    private async Task<decimal> GetGoalCommitmentsAsync(
        int year,
        int monthIndex,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        var (currentYear, currentMonth) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            _financialClock.Today, cycleDay);
        if (year != currentYear || monthIndex != currentMonth) return 0m;

        var active = await _context.SavingsGoals
            .AsNoTracking()
            .Where(goal => goal.Status == SavingsGoalStatus.Active)
            .ToListAsync(cancellationToken);
        if (active.Count == 0) return 0m;

        var cycleKey = StabilityRecoveryPlanner.CycleKey(currentYear, currentMonth);

        return active.Sum(goal => goal.EarmarkedAmount + SavingsGoals.SavingsGoalPacing
            .ComputePace(goal, _financialClock.Today, cycleDay, cycleKey).OutstandingThisCycle);
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
