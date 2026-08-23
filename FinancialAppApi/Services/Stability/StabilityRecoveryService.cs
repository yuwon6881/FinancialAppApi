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

internal sealed record GoalCommitments(decimal Essentials, decimal Rewards);

/// <summary>
/// Loads the state <see cref="StabilityRecoveryPlanner"/> needs and packages its output for the
/// dashboard. The EF half of the feature; every decision worth testing lives in the planner.
/// </summary>
public partial class StabilityRecoveryService
{
    private const string Stability = "Stability";

    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly FinancialClock _financialClock;
    private readonly StabilityPlanRevisionService _planRevisionService;

    public StabilityRecoveryService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        FinancialClock? financialClock = null,
        StabilityPlanRevisionService? planRevisionService = null)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _financialClock = financialClock ?? FinancialClock.Utc;
        _planRevisionService = planRevisionService ?? new StabilityPlanRevisionService(context);
    }

    private async Task BackfillCurrentCycleRecoveryIntentAsync(
        FinancialSetting setting,
        int year,
        int monthIndex,
        IReadOnlyList<Transaction> cycleTxs,
        IReadOnlyList<StabilityPlanSnapshot> planRevisions,
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
                Math.Max(
                    0m,
                    stabilityCredit - parent.Amount * StabilityPlanRevisionService.At(
                        planRevisions,
                        parent.PostedAt).StabilityAlloc),
                2,
                MidpointRounding.AwayFromZero);
            var stored = await _context.Transactions.FirstOrDefaultAsync(
                transaction => transaction.Id == parent.Id
                    && transaction.StabilityRecoveryTopUpAmount == null,
                cancellationToken);
            if (stored == null) continue;
            stored.StabilityRecoveryTopUpAmount = Math.Max(0m, inferred);
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
    /// has. The caller also supplies unpaid recurring Rewards bills. Active savings goals are then
    /// added to the matching bucket floor, so refilling the buffer never spends money already
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
        var planRevisions = await _planRevisionService.GetAsync(setting, cancellationToken);
        await BackfillCurrentCycleRecoveryIntentAsync(
            setting, year, monthIndex, activeCycleTxs, planRevisions, cancellationToken);

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
        var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, setting.CycleDay);
        var cycleStartUtc = DateTime.SpecifyKind(cycleStart, DateTimeKind.Utc);
        var cycleEndExclusiveUtc = DateTime.SpecifyKind(cycleEnd.Date.AddDays(1), DateTimeKind.Utc);
        var cycleEndInclusiveUtc = cycleEndExclusiveUtc.AddTicks(-1);
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
                activeCycleTxs,
                transaction => StabilityPlanRevisionService.At(
                    planRevisions,
                    transaction.PostedAt).StabilityAlloc));

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

        // FIFO repayment can consume part of a carried drawdown before touching one marked in the
        // current cycle. Keep the carried queue head in the reporting window as well: otherwise a
        // December 500 drawdown carried into January, followed by a January 300 drawdown and 600
        // reimbursement, would show only 300 marked and 100 repaid beside 200 outstanding.
        var windowDates = new[]
        {
            openingReload.OldestOutstandingDate,
            replay.OldestMarkedThisRunDate,
            replay.OldestOutstandingDate
        }
            .Where(date => date.HasValue)
            .Select(date => date!.Value)
            .ToList();
        var windowStart = replay.Outstanding > 0m && windowDates.Count > 0
            ? windowDates.Min()
            : (DateOnly?)null;

        var reloadTotals = replay.Outstanding > 0m && windowStart.HasValue
            ? await GetReloadTotalsAsync(
                windowStart.Value,
                year,
                monthIndex,
                setting.CycleDay,
                planRevisions,
                replay.Outstanding,
                cancellationToken)
            : (MarkedTotal: 0m, RepaidTotal: 0m);

        var goalCommitments = await GetGoalCommitmentsAsync(year, monthIndex, setting.CycleDay, cancellationToken);
        var committedEssentials = essentialsCommitted + goalCommitments.Essentials;
        var committedRewards = rewardsRecurringCommitted + goalCommitments.Rewards;

        return new StabilityRecoveryDto(
            replay.Outstanding > 0m,
            ObfuscationHelper.Obfuscate(reloadTotals.MarkedTotal),
            ObfuscationHelper.Obfuscate(
                StabilityPlanRevisionService.At(planRevisions, cycleEndInclusiveUtc).TargetStabilityFund),
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
            ObfuscationHelper.Obfuscate(committedEssentials),
            ObfuscationHelper.Obfuscate(committedRewards),
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
        IReadOnlyList<StabilityPlanSnapshot> planRevisions,
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
                transaction.StabilityReloadIntent,
                transaction.IsAccountBalanceAdjustment
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
                    StabilityReloadIntent = row.StabilityReloadIntent,
                    IsAccountBalanceAdjustment = row.IsAccountBalanceAdjustment
                }),
                transaction => StabilityPlanRevisionService.At(
                    planRevisions,
                    transaction.PostedAt).StabilityAlloc)
            .Where(movement => movement.Marked)
            .Sum(movement => Math.Max(0m, -movement.Change));
        return (markedTotal, Math.Max(0m, markedTotal - outstanding));
    }
}
