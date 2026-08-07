using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;

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
// boundaries retroactively). Invalidated cycles are simply deleted; the next dashboard read that
// needs them recomputes and re-caches on the fly via EnsureComputedThroughAsync.
public class CycleBalanceService
{
    // Matches the hardcoded roll-forward start year FinancialController used before snapshots
    // existed, so behavior for any (hypothetical) pre-2026 transaction data is unchanged.
    private const int BaselineYear = 2026;

    private readonly AppDbContext _context;

    // Opening balances resolved so far in this request. The service is scoped, so the lifetime is
    // one request -- long enough to matter (a single /api/bootstrap resolves the same cycle twice,
    // once for the dashboard and once for the wallet balance, and SavingsGoalService,
    // StabilityRecoveryService and the AI context loaders each ask again) and short enough that
    // nothing outside the request can invalidate it behind our back. Inside the request the only
    // thing that can is Invalidate*Async, which clears it.
    private readonly Dictionary<(int year, int monthIndex, int cycleDay), (decimal, decimal, decimal, decimal)> _openingBalances = new();

    public CycleBalanceService(AppDbContext context)
    {
        _context = context;
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

        decimal essentials = 0m, growth = 0m, stability = 0m, rewards = 0m;
        int fromYear = startYear, fromMonth = 1;

        if (last != null)
        {
            essentials = last.EssentialsBalance;
            growth = last.GrowthBalance;
            stability = last.StabilityBalance;
            rewards = last.RewardsBalance;
            fromYear = last.MonthIndex == 12 ? last.Year + 1 : last.Year;
            fromMonth = last.MonthIndex == 12 ? 1 : last.MonthIndex + 1;
        }

        CycleBalance current = last!;

        var firstRange = CategoryAttributionService.GetCycleRange(fromYear, fromMonth, cycleDay);
        var finalRange = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        var firstDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(firstRange.start));
        var finalEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(finalRange.end));
        var timelineTransactions = await _context.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.Date >= firstDate && transaction.Date < finalEndExclusive)
            .ToListAsync(cancellationToken);

        for (int y = fromYear; y <= year; y++)
        {
            int monthFrom = y == fromYear ? fromMonth : 1;
            int monthTo = y == year ? monthIndex : 12;

            for (int m = monthFrom; m <= monthTo; m++)
            {
                var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(y, m, cycleDay);
                var cycleStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(cycleStart));
                var cycleEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(cycleEnd));

                var cycleTxs = timelineTransactions
                    .Where(transaction =>
                        transaction.Date >= cycleStartDate &&
                        transaction.Date < cycleEndExclusive)
                    .ToList();

                essentials += cycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Essentials"));
                growth += cycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Growth"));
                stability += cycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Stability"));
                rewards += cycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Rewards"));

                current = new CycleBalance
                {
                    Year = y,
                    MonthIndex = m,
                    EssentialsBalance = essentials,
                    GrowthBalance = growth,
                    StabilityBalance = stability,
                    RewardsBalance = rewards
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
