using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;
using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Services;

// Persists the 4 budget buckets' cumulative ending balance per closed cycle so the dashboard
// only has to replay forward from the last cached cycle instead of the app's entire transaction
// history on every request. Balances carry forward cycle-to-cycle (never reset), matching
// FinancialController's original in-memory roll-forward loop exactly -- this class computes the
// same per-cycle net changes (via CategoryAttributionService.GetCategoryAmount) and caches the running
// total instead of recomputing it from year 2026 on every dashboard load.
//
// Invalidation: because any past transaction can be edited/deleted, a change dated in cycle X
// can change the ending balance of every cycle from X onward. Callers must invoke
// InvalidateFromAsync(X) whenever a transaction dated at or before a cached cycle changes, and
// InvalidateAllAsync() whenever the CycleDay setting changes (that shifts every cycle's date
// boundaries retroactively), or whenever a Stability reload setting changes (the replay target
// and normal Stability share affect cached obligations). Invalidated cycles are simply deleted;
// the next dashboard read that needs them recomputes and re-caches on the fly via
// EnsureComputedThroughAsync.
public class CycleBalanceService
{
    // Matches the hardcoded roll-forward start year FinancialController used before snapshots
    // existed, so behavior for any (hypothetical) pre-2026 transaction data is unchanged.
    private const int BaselineYear = 2026;

    private readonly AppDbContext _context;
    private readonly StabilityPlanRevisionService _stabilityPlanRevisionService;

    // Opening balances resolved so far in this request. The service is scoped, so the lifetime is
    // one request -- long enough to matter (a single /api/bootstrap resolves the same cycle twice,
    // once for the dashboard and once for the wallet balance, and SavingsGoalService,
    // StabilityRecoveryService and the AI context loaders each ask again) and short enough that
    // nothing outside the request can invalidate it behind our back. Inside the request the only
    // thing that can is Invalidate*Async, which clears it.
    private readonly Dictionary<(int year, int monthIndex, int cycleDay), (decimal, decimal, decimal, decimal)> _openingBalances = new();

    // Same request-scoped memo, for the per-account view of the same snapshot. /api/bootstrap asks
    // for it twice (wallet balance and the account rows), and re-reading would re-run the walk.
    private readonly Dictionary<(int year, int monthIndex, int cycleDay), IReadOnlyDictionary<string, decimal>?> _openingAccountBalances = new();

    public CycleBalanceService(
        AppDbContext context,
        StabilityPlanRevisionService? stabilityPlanRevisionService = null)
    {
        _context = context;
        _stabilityPlanRevisionService = stabilityPlanRevisionService
            ?? new StabilityPlanRevisionService(context);
    }

    // Ensures a CycleBalance row exists for every cycle from this timeline's start through
    // (year, monthIndex) inclusive, computing any missing ones by walking forward from the
    // latest cached snapshot and persisting each new row as it goes. Returns the row for the
    // requested cycle.
    public async Task<CycleBalance> EnsureComputedThroughAsync(
        int year,
        int monthIndex,
        int cycleDay,
        CancellationToken cancellationToken = default)
    {
        var startYear = Math.Min(BaselineYear, year);

        var last = await _context.CycleBalances
            .Where(b => b.Year >= startYear && (b.Year < year || (b.Year == year && b.MonthIndex <= monthIndex)))
            .OrderByDescending(b => b.Year)
            .ThenByDescending(b => b.MonthIndex)
            .FirstOrDefaultAsync(cancellationToken);

        if (last != null && last.Year == year && last.MonthIndex == monthIndex)
        {
            return last;
        }

        var setting = await _context.FinancialSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        var planRevisions = setting == null
            ? [new StabilityPlanSnapshot(DateTime.UnixEpoch, 0m, 0m)]
            : await _stabilityPlanRevisionService.GetAsync(setting, cancellationToken);

        // Needed to place each bucket leg into an account. Loaded once for the whole walk; the set
        // is small and, because an account with ledger activity cannot change bucket, the placement
        // it produces for an already-dated transaction never changes retroactively.
        var accountsById = await _context.LedgerAccounts
            .AsNoTracking()
            .ToDictionaryAsync(account => account.Id, StringComparer.Ordinal, cancellationToken);

        decimal essentials = 0m, growth = 0m, stability = 0m, rewards = 0m;
        var reloadState = last == null
            ? new ReloadState(0m, null, 0m, 0m)
            : StabilityReloadObligationCache.OpeningState(
                last.StabilityReloadOutstanding,
                last.StabilityReloadOldestDate,
                last.StabilityReloadObligations);
        int fromYear = startYear, fromMonth = 1;

        // Running per-account balances for the walk. Null means the previous row predates the
        // AccountBalances column and cannot seed it; recovered below once firstDate is known.
        Dictionary<string, decimal>? accountBalances =
            last == null ? new Dictionary<string, decimal>(StringComparer.Ordinal) : null;

        if (last != null)
        {
            essentials = last.EssentialsBalance;
            growth = last.GrowthBalance;
            stability = last.StabilityBalance;
            rewards = last.RewardsBalance;
            fromYear = last.MonthIndex == 12 ? last.Year + 1 : last.Year;
            fromMonth = last.MonthIndex == 12 ? 1 : last.MonthIndex + 1;

            var cachedAccounts = LedgerAccountBalanceCache.Deserialize(last.AccountBalances);
            if (cachedAccounts is not null)
            {
                accountBalances = new Dictionary<string, decimal>(cachedAccounts, StringComparer.Ordinal);
            }
        }

        CycleBalance current = last!;

        var firstRange = CategoryAttributionService.GetCycleRange(fromYear, fromMonth, cycleDay);
        var finalRange = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        var firstDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(firstRange.start));
        var finalEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(finalRange.end));
        // A row written before the AccountBalances column existed cannot seed the account tally.
        // One scan of everything preceding this walk recovers it; because every row written below
        // carries the column, no later read has to pay for this again.
        if (accountBalances is null)
        {
            accountBalances = new Dictionary<string, decimal>(StringComparer.Ordinal);
            await foreach (var transaction in _context.Transactions
                .AsNoTracking()
                .Where(transaction => transaction.Date < firstDate)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken))
            {
                LedgerAccountBalanceMath.Accumulate(accountBalances, transaction, accountsById);
            }
        }

        var timelineTransactions = await _context.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.Date >= firstDate && transaction.Date < finalEndExclusive)
            .OrderBy(transaction => transaction.Date)
            .ThenBy(transaction => transaction.PostedAt)
            .ThenBy(transaction => transaction.Id)
            .ToListAsync(cancellationToken);
        // Sorted once so each cycle can binary-search its own slice below. Normally only the
        // current cycle is missing and this costs nothing, but a backdated edit deletes every
        // snapshot from its cycle forward, so the loop can span years of history -- and
        // re-filtering the whole timeline inside it made that rebuild quadratic in
        // cycles x transactions. DescribeAll re-sorts by this same key, so the slices below are
        // in the order it would have produced anyway.
        var timelineDates = timelineTransactions.Select(transaction => transaction.Date).ToList();

        // Index of the first transaction dated at or after `value`, over the sorted list above.
        int LowerBound(DateTime value)
        {
            int low = 0, high = timelineDates.Count;
            while (low < high)
            {
                var mid = low + ((high - low) / 2);
                if (timelineDates[mid] < value) low = mid + 1;
                else high = mid;
            }
            return low;
        }

        for (int y = fromYear; y <= year; y++)
        {
            int monthFrom = y == fromYear ? fromMonth : 1;
            int monthTo = y == year ? monthIndex : 12;

            for (int m = monthFrom; m <= monthTo; m++)
            {
                var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(y, m, cycleDay);
                var cycleStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(cycleStart));
                var cycleEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(cycleEnd));

                var sliceStart = LowerBound(cycleStartDate);
                var cycleTxs = timelineTransactions.GetRange(
                    sliceStart,
                    LowerBound(cycleEndExclusive) - sliceStart);

                var stabilityOpening = stability;
                var cycleStartUtc = DateTime.SpecifyKind(cycleStart, DateTimeKind.Utc);
                var cycleEndExclusiveUtc = DateTime.SpecifyKind(cycleEnd.Date.AddDays(1), DateTimeKind.Utc);
                var planAtStart = StabilityPlanRevisionService.At(planRevisions, cycleStartUtc);
                var cyclePlanPoints = planRevisions
                    .Where(revision => revision.EffectiveAt > cycleStartUtc && revision.EffectiveAt < cycleEndExclusiveUtc)
                    .Select(revision => new ReloadPlanPoint(revision.EffectiveAt, revision.TargetStabilityFund))
                    .Prepend(new ReloadPlanPoint(planAtStart.EffectiveAt, planAtStart.TargetStabilityFund))
                    .ToList();
                var reloaded = StabilityReloadLedger.Replay(
                    reloadState,
                    stabilityOpening,
                    cyclePlanPoints,
                    StabilityReloadLedger.DescribeAll(
                        cycleTxs,
                        transaction => StabilityPlanRevisionService.At(
                            planRevisions,
                            transaction.PostedAt).StabilityAlloc));
                // Carry only the still-owing entries. A discharged obligation has nothing left to
                // hand to the next cycle, and keeping them would grow the dictionary for the whole
                // walk. This is the same set the row below persists.
                var openObligations = (reloaded.Obligations ?? [])
                    .Where(obligation => obligation.RemainingAmount > 0m)
                    .ToList();
                reloadState = new ReloadState(
                    reloaded.Outstanding,
                    reloaded.OldestOutstandingDate,
                    0m,
                    0m,
                    openObligations);

                // One pass for all four buckets rather than four passes over the same slice, and the
                // per-account placement of each leg is taken from the same pass.
                foreach (var transaction in cycleTxs)
                {
                    essentials += CategoryAttributionService.GetCategoryAmount(transaction, "Essentials");
                    growth += CategoryAttributionService.GetCategoryAmount(transaction, "Growth");
                    stability += CategoryAttributionService.GetCategoryAmount(transaction, "Stability");
                    rewards += CategoryAttributionService.GetCategoryAmount(transaction, "Rewards");
                    LedgerAccountBalanceMath.Accumulate(accountBalances, transaction, accountsById);
                }

                current = new CycleBalance
                {
                    Year = y,
                    MonthIndex = m,
                    EssentialsBalance = essentials,
                    GrowthBalance = growth,
                    StabilityBalance = stability,
                    StabilityReloadOutstanding = reloaded.Outstanding,
                    StabilityReloadOldestDate = reloaded.OldestOutstandingDate,
                    StabilityReloadObligations = StabilityReloadObligationCache.Serialize(openObligations),
                    RewardsBalance = rewards,
                    AccountBalances = LedgerAccountBalanceCache.Serialize(accountBalances)
                };
                _context.CycleBalances.Add(current);
            }
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another concurrent request (e.g. duplicate dashboard loads from a double-mounted
            // effect, or a second browser tab) already computed and persisted an overlapping
            // range, so this insert collided on the (Year, MonthIndex) key. Discard our pending
            // rows and read back whatever is now persisted instead of failing the request.
            foreach (var entry in _context.ChangeTracker.Entries<CycleBalance>().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            var existing = await _context.CycleBalances
                .FirstOrDefaultAsync(
                    b => b.Year == year && b.MonthIndex == monthIndex,
                    cancellationToken);
            if (existing != null)
            {
                return existing;
            }

            throw;
        }

        return current;
    }

    // Returns the opening (carried-forward) balance for (year, monthIndex) -- i.e. the ending
    // balance of the cycle immediately before it -- computing/caching everything up to that
    // prior cycle if needed. Zero if (year, monthIndex) is the very first cycle of its timeline.
    public async Task<(decimal essentials, decimal growth, decimal stability, decimal rewards)> GetOpeningBalanceAsync(
        int year,
        int monthIndex,
        int cycleDay,
        CancellationToken cancellationToken = default)
    {
        var startYear = Math.Min(BaselineYear, year);
        if (year == startYear && monthIndex == 1)
        {
            return (0m, 0m, 0m, 0m);
        }

        if (_openingBalances.TryGetValue((year, monthIndex, cycleDay), out var memoized))
        {
            return memoized;
        }

        var prevYear = monthIndex == 1 ? year - 1 : year;
        var prevMonth = monthIndex == 1 ? 12 : monthIndex - 1;

        var row = await EnsureComputedThroughAsync(
            prevYear,
            prevMonth,
            cycleDay,
            cancellationToken);
        var opening = (row.EssentialsBalance, row.GrowthBalance, row.StabilityBalance, row.RewardsBalance);
        _openingBalances[(year, monthIndex, cycleDay)] = opening;
        return opening;
    }

    // Returns the cumulative per-account balances carried into (year, monthIndex) -- i.e. the
    // ending balances of the cycle immediately before it -- or null when there is no usable cached
    // row, in which case the caller must answer from the full transaction history instead. Empty
    // (not null) for the very first cycle of the timeline.
    //
    // Deliberately read-only: unlike GetOpeningBalanceAsync this never triggers the forward walk.
    // The walk is expensive on a cold cache, and the dashboard runs it moments later anyway, so
    // forcing it here would move that cost onto every caller instead of removing it. A miss simply
    // means the caller scans this once; the next request finds the row the dashboard wrote.
    public async Task<IReadOnlyDictionary<string, decimal>?> GetOpeningAccountBalancesAsync(
        int year,
        int monthIndex,
        int cycleDay,
        CancellationToken cancellationToken = default)
    {
        var startYear = Math.Min(BaselineYear, year);
        if (year == startYear && monthIndex == 1)
        {
            return new Dictionary<string, decimal>(StringComparer.Ordinal);
        }

        if (_openingAccountBalances.TryGetValue((year, monthIndex, cycleDay), out var memoized))
        {
            return memoized;
        }

        var prevYear = monthIndex == 1 ? year - 1 : year;
        var prevMonth = monthIndex == 1 ? 12 : monthIndex - 1;
        var row = await _context.CycleBalances
            .AsNoTracking()
            .FirstOrDefaultAsync(
                balance => balance.Year == prevYear && balance.MonthIndex == prevMonth,
                cancellationToken);
        var opening = row is null ? null : LedgerAccountBalanceCache.Deserialize(row.AccountBalances);
        _openingAccountBalances[(year, monthIndex, cycleDay)] = opening;
        return opening;
    }

    // Deletes every cached snapshot for cycles at or after (year, monthIndex) -- call whenever a
    // transaction dated in or before that cycle is created/edited/deleted, since the running
    // balance for every cycle downstream of it is now stale.
    public async Task InvalidateFromAsync(
        int year,
        int monthIndex,
        CancellationToken cancellationToken = default)
    {
        // Clear the whole memo rather than just the cycles at or after (year, monthIndex): a
        // mutation that invalidates cycle X within a request must not let an opening balance
        // resolved earlier in that same request survive, and the memo is a handful of entries.
        _openingBalances.Clear();
        _openingAccountBalances.Clear();

        var query = _context.CycleBalances
            .Where(b => b.Year > year || (b.Year == year && b.MonthIndex >= monthIndex));

        if (_context.Database.IsRelational())
        {
            await query.ExecuteDeleteAsync(cancellationToken);
            return;
        }

        _context.CycleBalances.RemoveRange(await query.ToListAsync(cancellationToken));
        await _context.SaveChangesAsync(cancellationToken);
    }

    // Deletes every cached snapshot outright -- call when the CycleDay setting changes, since
    // that shifts every cycle's date boundaries retroactively and invalidates the whole history.
    public async Task InvalidateAllAsync(CancellationToken cancellationToken = default)
    {
        _openingBalances.Clear();
        _openingAccountBalances.Clear();

        if (_context.Database.IsRelational())
        {
            await _context.CycleBalances.ExecuteDeleteAsync(cancellationToken);
            return;
        }

        _context.CycleBalances.RemoveRange(
            await _context.CycleBalances.ToListAsync(cancellationToken));
        await _context.SaveChangesAsync(cancellationToken);
    }
}
