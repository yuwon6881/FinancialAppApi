using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Stability;

/// <summary>
/// Produces the per-withdrawal status used by Ledger rows. The status is derived from the same full
/// FIFO replay as the dashboard, while plan revisions make target attainment durable across later
/// target edits.
/// </summary>
public sealed class StabilityReloadStatusService
{
    private readonly AppDbContext _context;
    private readonly StabilityPlanRevisionService _planRevisionService;

    public StabilityReloadStatusService(
        AppDbContext context,
        StabilityPlanRevisionService? planRevisionService = null)
    {
        _context = context;
        _planRevisionService = planRevisionService ?? new StabilityPlanRevisionService(context);
    }

    /// <param name="cancellationToken">Cancels the reads this replay performs.</param>
    /// <param name="forTransactionIds">
    /// The rows whose status the caller will actually render. Supplying them lets the replay start
    /// at the cycle boundary before the earliest of them instead of at the beginning of time, which
    /// is what stops a single page of the Ledger from costing a full-history replay. Passing null
    /// keeps the original whole-history behavior.
    /// </param>
    public async Task<IReadOnlyDictionary<string, string>> GetStatusMapAsync(
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? forTransactionIds = null)
    {
        var setting = await _context.FinancialSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (setting == null) return new Dictionary<string, string>(StringComparer.Ordinal);

        var revisions = await _planRevisionService.GetAsync(setting, cancellationToken);
        var seed = await ResolveSeedAsync(setting, forTransactionIds, cancellationToken);

        var query = _context.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.LedgerCategory != "Discarded");
        if (seed is not null)
        {
            var from = seed.Value.From;
            query = query.Where(transaction => transaction.Date >= from);
        }

        var transactions = await query
            .OrderBy(transaction => transaction.Date)
            .ThenBy(transaction => transaction.PostedAt)
            .ThenBy(transaction => transaction.Id)
            .ToListAsync(cancellationToken);
        var movements = StabilityReloadLedger.DescribeAll(
            transactions,
            transaction => StabilityPlanRevisionService.At(
                revisions,
                transaction.PostedAt).StabilityAlloc);
        var state = StabilityReloadLedger.Replay(
            seed?.Opening ?? new ReloadState(0m, null, 0m, 0m),
            openingBalance: seed?.OpeningBalance ?? 0m,
            revisions.Select(revision => new ReloadPlanPoint(
                revision.EffectiveAt,
                revision.TargetStabilityFund)),
            movements);
        var obligations = state.Obligations?.ToDictionary(
            obligation => obligation.TransactionId,
            StringComparer.Ordinal)
            ?? new Dictionary<string, ReloadObligation>(StringComparer.Ordinal);
        var transactionsById = transactions.ToDictionary(
            transaction => transaction.Id,
            StringComparer.Ordinal);
        var status = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var movement in movements.Where(item => item.Change < 0m && item.TransactionId is not null))
        {
            var id = movement.TransactionId!;
            if (!transactionsById.TryGetValue(id, out var transaction)) continue;
            if (transaction.IsAccountBalanceAdjustment) continue;
            var intent = StabilityReloadIntent.Normalize(transaction.StabilityReloadIntent);
            if (intent == StabilityReloadIntent.NotRequired)
            {
                status[id] = StabilityReloadStatus.NotRequired;
                continue;
            }

            var original = Math.Max(0m, -movement.Change);
            var remaining = obligations.TryGetValue(id, out var obligation)
                ? Math.Clamp(obligation.RemainingAmount, 0m, original)
                : original;
            status[id] = remaining <= 0m
                ? StabilityReloadStatus.Complete
                : remaining < original
                    ? StabilityReloadStatus.PartlyRepaid
                    : StabilityReloadStatus.Outstanding;
        }

        return status;
    }

    private readonly record struct ReplaySeed(DateTime From, ReloadState Opening, decimal OpeningBalance);

    /// <summary>
    /// Finds a cycle boundary the replay can start from, or null to replay everything.
    /// <para>
    /// The boundary must sit at or before the earliest row the caller wants a status for, so every
    /// obligation those rows create is formed inside the replayed window. Obligations opened before
    /// it are carried in via the cached FIFO queue, which is what keeps a later repayment
    /// discharging the right one -- see STAB-10.
    /// </para>
    /// </summary>
    private async Task<ReplaySeed?> ResolveSeedAsync(
        FinancialSetting setting,
        IReadOnlyCollection<string>? forTransactionIds,
        CancellationToken cancellationToken)
    {
        if (forTransactionIds is null || forTransactionIds.Count == 0) return null;

        var earliest = await _context.Transactions
            .AsNoTracking()
            .Where(transaction => forTransactionIds.Contains(transaction.Id))
            .Select(transaction => (DateTime?)transaction.Date)
            .MinAsync(cancellationToken);
        if (earliest is null) return null;

        var cycleDay = setting.CycleDay;
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            DateOnly.FromDateTime(earliest.Value),
            cycleDay);
        var prevYear = monthIndex == 1 ? year - 1 : year;
        var prevMonth = monthIndex == 1 ? 12 : monthIndex - 1;

        // Read-only on purpose: a miss just means the caller replays the whole history this once,
        // which is strictly the old behavior. Forcing the cycle walk here would trade the scan for
        // an equally expensive rebuild.
        var row = await _context.CycleBalances
            .AsNoTracking()
            .FirstOrDefaultAsync(
                balance => balance.Year == prevYear && balance.MonthIndex == prevMonth,
                cancellationToken);
        if (row is null) return null;

        var range = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        return new ReplaySeed(
            TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start)),
            StabilityReloadObligationCache.OpeningState(
                row.StabilityReloadOutstanding,
                row.StabilityReloadOldestDate,
                row.StabilityReloadObligations),
            row.StabilityBalance);
    }
}
