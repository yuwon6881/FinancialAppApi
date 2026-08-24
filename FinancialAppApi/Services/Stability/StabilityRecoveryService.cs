using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Stability;

/// <summary>One contributing bucket's share of a proposed top-up. A ratio, so not obfuscated.</summary>
public sealed record StabilityRecoveryDrawDto(string Bucket, decimal Share);

/// <summary>
/// One still-owing drawdown carried into this cycle, so the client's optimistic replay can start
/// from the same queue the server did. Without the identities the client seeds one anonymous entry
/// and cannot tell a partly-repaid drawdown from one already put back in full.
/// </summary>
public sealed record StabilityReloadObligationDto(
    string TransactionId,
    string OriginalAmount,
    string RemainingAmount,
    string? Date);

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
    IReadOnlyList<StabilityReloadObligationDto> OpeningObligations,
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

        // Only the cycle immediately before this one is needed: it carries the whole queue forward,
        // identities included. Nothing reads further back now that the pace anchors on the oldest
        // obligation still owing rather than on the last cycle that recorded a marked amount.
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

        // The pace is anchored on the oldest drawdown that still owes money, which FIFO order puts
        // at the head of the queue. Anchoring on the last cycle that recorded any marked amount kept
        // a drawdown that has since been put back in full driving the deadline -- and reporting the
        // recovery as overdue on the strength of money that is already back.
        var anchorDate = replay.Outstanding > 0m ? replay.OldestOutstandingDate : null;
        string? lastDrawdownCycleKey = null;
        if (anchorDate.HasValue)
        {
            var (anchorYear, anchorMonthIndex) = CategoryAttributionService
                .GetCycleYearAndMonthIndexForDate(anchorDate.Value, setting.CycleDay);
            lastDrawdownCycleKey = StabilityRecoveryPlanner.CycleKey(anchorYear, anchorMonthIndex);
        }
        var currentCycleKey = StabilityRecoveryPlanner.CycleKey(year, monthIndex);
        var cyclesRemaining = StabilityRecoveryPlanner.CyclesRemaining(
            lastDrawdownCycleKey, currentCycleKey, FinancialConstants.StabilityRecoveryCycles);
        var pace = StabilityRecoveryPlanner.ComputePace(
            replay.Outstanding,
            cyclesRemaining,
            replay.RepaidThisRun);

        var goalCommitments = await GetGoalCommitmentsAsync(year, monthIndex, setting.CycleDay, cancellationToken);
        var committedEssentials = essentialsCommitted + goalCommitments.Essentials;
        var committedRewards = rewardsRecurringCommitted + goalCommitments.Rewards;

        return new StabilityRecoveryDto(
            replay.Outstanding > 0m,
            ObfuscationHelper.Obfuscate(replay.OpenMarkedTotal),
            ObfuscationHelper.Obfuscate(
                StabilityPlanRevisionService.At(planRevisions, cycleEndInclusiveUtc).TargetStabilityFund),
            ObfuscationHelper.Obfuscate(currentStability),
            ObfuscationHelper.Obfuscate(replay.Outstanding),
            ObfuscationHelper.Obfuscate(openingReload.Outstanding),
            openingReload.OldestOutstandingDate?.ToString("yyyy-MM-dd"),
            (openingReload.Obligations ?? [])
                .Where(obligation => obligation.RemainingAmount > 0m)
                .Select(obligation => new StabilityReloadObligationDto(
                    obligation.TransactionId,
                    ObfuscationHelper.Obfuscate(obligation.OriginalAmount),
                    ObfuscationHelper.Obfuscate(obligation.RemainingAmount),
                    obligation.Date?.ToString("yyyy-MM-dd")))
                .ToList(),
            pace.CyclesRemaining,
            ObfuscationHelper.Obfuscate(pace.RequiredThisCycle),
            ObfuscationHelper.Obfuscate(pace.ToppedUpThisCycle),
            ObfuscationHelper.Obfuscate(pace.OutstandingThisCycle),
            pace.IsOverdue,
            lastDrawdownCycleKey,
            ObfuscationHelper.Obfuscate(replay.OpenRepaidTotal),
            ObfuscationHelper.Obfuscate(committedEssentials),
            ObfuscationHelper.Obfuscate(committedRewards),
            BuildSuggestedDraws(setting),
            // The oldest drawdown that still owes money is exactly the set the totals above cover, so
            // "see every movement since then" lands on the rows those figures came from.
            anchorDate?.ToString("yyyy-MM-dd"));
    }
}
