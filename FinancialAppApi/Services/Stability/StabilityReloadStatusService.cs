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
    private readonly FinancialClock _clock;

    public StabilityReloadStatusService(
        AppDbContext context,
        StabilityPlanRevisionService? planRevisionService = null,
        FinancialClock? clock = null)
    {
        _context = context;
        _planRevisionService = planRevisionService ?? new StabilityPlanRevisionService(context);
        _clock = clock ?? FinancialClock.Utc;
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

        var requestedTransactions = forTransactionIds is { Count: > 0 }
            ? await _context.Transactions
                .AsNoTracking()
                .Where(transaction => forTransactionIds.Contains(transaction.Id))
                .ToListAsync(cancellationToken)
            : null;
        if (requestedTransactions is { Count: 0 })
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var revisions = await _planRevisionService.GetAsync(setting, cancellationToken);
        var seed = await ResolveSeedAsync(setting, forTransactionIds is { Count: > 0 }, cancellationToken);
        var replay = await ReplayAsync(revisions, seed, cancellationToken);
        var transactions = replay.Transactions;
        var state = replay.State;
        var obligations = state.Obligations?.ToDictionary(
            obligation => obligation.TransactionId,
            StringComparer.Ordinal)
            ?? new Dictionary<string, ReloadObligation>(StringComparer.Ordinal);
        var status = new Dictionary<string, string>(StringComparer.Ordinal);

        // A seeded replay only loads the current-cycle tail. Older requested withdrawals are
        // classified from the FIFO identities carried into that boundary: present means still
        // owing, absent means they were already discharged. Current-cycle movements remain in the
        // replay result (including zero-remaining obligations), so the same lookup covers both.
        var candidates = requestedTransactions ?? transactions;
        foreach (var transaction in candidates)
        {
            if (transaction.IsAccountBalanceAdjustment) continue;
            var described = StabilityReloadLedger.Describe(
                transaction,
                StabilityPlanRevisionService.At(revisions, transaction.PostedAt).StabilityAlloc);
            if (described is not { Change: < 0m } movement) continue;

            var id = transaction.Id;
            var intent = StabilityReloadIntent.Normalize(transaction.StabilityReloadIntent);
            if (intent == StabilityReloadIntent.NotRequired)
            {
                status[id] = StabilityReloadStatus.NotRequired;
                continue;
            }

            var original = Math.Max(0m, -movement.Change);
            var remaining = obligations.TryGetValue(id, out var obligation)
                ? Math.Clamp(obligation.RemainingAmount, 0m, original)
                : seed is not null && transaction.Date < seed.Value.From
                    ? 0m
                    : original;
            status[id] = remaining <= 0m
                ? StabilityReloadStatus.Complete
                : remaining < original
                    ? StabilityReloadStatus.PartlyRepaid
                    : StabilityReloadStatus.Outstanding;
        }

        return status;
    }

    /// <summary>
    /// Returns only withdrawals that still have money owing. The healthy path starts from the
    /// latest closed-cycle FIFO snapshot and replays the current tail, so filtering a deep Ledger
    /// does not scan history in proportion to the account's age.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetOpenStatusMapAsync(
        CancellationToken cancellationToken = default)
    {
        var setting = await _context.FinancialSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (setting == null) return new Dictionary<string, string>(StringComparer.Ordinal);

        var revisions = await _planRevisionService.GetAsync(setting, cancellationToken);
        var seed = await ResolveSeedAsync(setting, useLatestBoundary: true, cancellationToken);
        var replay = await ReplayAsync(revisions, seed, cancellationToken);

        return (replay.State.Obligations ?? [])
            .Where(obligation => obligation.RemainingAmount > 0m)
            .ToDictionary(
                obligation => obligation.TransactionId,
                obligation => obligation.RemainingAmount < obligation.OriginalAmount
                    ? StabilityReloadStatus.PartlyRepaid
                    : StabilityReloadStatus.Outstanding,
                StringComparer.Ordinal);
    }

    private readonly record struct ReplaySeed(DateTime From, ReloadState Opening, decimal OpeningBalance);
    private readonly record struct StatusReplay(IReadOnlyList<Transaction> Transactions, ReloadState State);

    private async Task<StatusReplay> ReplayAsync(
        IReadOnlyList<StabilityPlanSnapshot> revisions,
        ReplaySeed? seed,
        CancellationToken cancellationToken)
    {
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
            PlanPointsFrom(revisions, seed?.From),
            movements);
        return new StatusReplay(transactions, state);
    }

    /// <summary>
    /// Finds the latest closed-cycle boundary the replay can start from, or null to replay
    /// everything.
    /// <para>
    /// Obligations opened before it are carried in via the cached FIFO queue. That queue is also
    /// sufficient to classify any requested historical withdrawal: if its identity is absent, it
    /// was already settled before the boundary. This keeps deep historical pages bounded to their
    /// requested rows plus the current-cycle tail instead of replaying every later year.
    /// </para>
    /// </summary>
    private async Task<ReplaySeed?> ResolveSeedAsync(
        FinancialSetting setting,
        bool useLatestBoundary,
        CancellationToken cancellationToken)
    {
        if (!useLatestBoundary) return null;

        var cycleDay = setting.CycleDay;
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            _clock.Today,
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

        var obligations = StabilityReloadObligationCache.Deserialize(row.StabilityReloadObligations);
        // A positive aggregate without matching identities is a legacy/corrupt cache row. It can
        // still seed dashboard totals, but it cannot say which historical withdrawal remains open,
        // so per-row status must fall back to the authoritative full replay.
        var obligationTotal = obligations?.Sum(obligation => obligation.RemainingAmount) ?? 0m;
        if (obligationTotal != row.StabilityReloadOutstanding)
        {
            if (row.StabilityReloadOutstanding > 0m) return null;
            obligations = null;
        }

        var range = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        return new ReplaySeed(
            TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start)),
            new ReloadState(
                row.StabilityReloadOutstanding,
                row.StabilityReloadOldestDate,
                0m,
                0m,
                obligations),
            row.StabilityBalance);
    }

    private static IEnumerable<ReloadPlanPoint> PlanPointsFrom(
        IReadOnlyList<StabilityPlanSnapshot> revisions,
        DateTime? from)
    {
        if (from is null)
        {
            return revisions.Select(revision => new ReloadPlanPoint(
                revision.EffectiveAt,
                revision.TargetStabilityFund));
        }

        var boundary = DateTime.SpecifyKind(from.Value, DateTimeKind.Utc);
        var active = StabilityPlanRevisionService.At(revisions, boundary);
        return revisions
            .Where(revision => revision.EffectiveAt > boundary)
            .Select(revision => new ReloadPlanPoint(revision.EffectiveAt, revision.TargetStabilityFund))
            .Prepend(new ReloadPlanPoint(boundary, active.TargetStabilityFund));
    }
}
