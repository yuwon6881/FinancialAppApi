using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Controllers;
using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services;

// Persists the 4 budget buckets' cumulative ending balance per closed cycle so the dashboard
// only has to replay forward from the last cached cycle instead of the app's entire transaction
// history on every request. Balances carry forward cycle-to-cycle (never reset), matching
// FinancialController's original in-memory roll-forward loop exactly -- this class computes the
// same per-cycle net changes (via FinancialController.GetCategoryAmount) and caches the running
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

    public CycleBalanceService(AppDbContext context)
    {
        _context = context;
    }

    // Ensures a CycleBalance row exists for every cycle from this timeline's start through
    // (year, monthIndex) inclusive, computing any missing ones by walking forward from the
    // latest cached snapshot and persisting each new row as it goes. Returns the row for the
    // requested cycle.
    public async Task<CycleBalance> EnsureComputedThroughAsync(int year, int monthIndex, int cycleDay)
    {
        var startYear = Math.Min(BaselineYear, year);

        var last = await _context.CycleBalances
            .Where(b => b.Year >= startYear && (b.Year < year || (b.Year == year && b.MonthIndex <= monthIndex)))
            .OrderByDescending(b => b.Year)
            .ThenByDescending(b => b.MonthIndex)
            .FirstOrDefaultAsync();

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

        for (int y = fromYear; y <= year; y++)
        {
            int monthFrom = y == fromYear ? fromMonth : 1;
            int monthTo = y == year ? monthIndex : 12;

            for (int m = monthFrom; m <= monthTo; m++)
            {
                var (cycleStart, cycleEnd, _) = FinancialController.GetCycleRange(y, m, cycleDay);
                var cycleStartDate = DateOnly.FromDateTime(cycleStart);
                var cycleEndDate = DateOnly.FromDateTime(cycleEnd);

                var cycleTxs = await _context.Transactions
                    .AsNoTracking()
                    .Where(t => t.Date >= cycleStartDate && t.Date <= cycleEndDate)
                    .ToListAsync();

                essentials += cycleTxs.Sum(t => FinancialController.GetCategoryAmount(t, "Essentials"));
                growth += cycleTxs.Sum(t => FinancialController.GetCategoryAmount(t, "Growth"));
                stability += cycleTxs.Sum(t => FinancialController.GetCategoryAmount(t, "Stability"));
                rewards += cycleTxs.Sum(t => FinancialController.GetCategoryAmount(t, "Rewards"));

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
            await _context.SaveChangesAsync();
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
                .FirstOrDefaultAsync(b => b.Year == year && b.MonthIndex == monthIndex);
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
        int year, int monthIndex, int cycleDay)
    {
        var startYear = Math.Min(BaselineYear, year);
        if (year == startYear && monthIndex == 1)
        {
            return (0m, 0m, 0m, 0m);
        }

        var prevYear = monthIndex == 1 ? year - 1 : year;
        var prevMonth = monthIndex == 1 ? 12 : monthIndex - 1;

        var row = await EnsureComputedThroughAsync(prevYear, prevMonth, cycleDay);
        return (row.EssentialsBalance, row.GrowthBalance, row.StabilityBalance, row.RewardsBalance);
    }

    // Deletes every cached snapshot for cycles at or after (year, monthIndex) -- call whenever a
    // transaction dated in or before that cycle is created/edited/deleted, since the running
    // balance for every cycle downstream of it is now stale.
    public async Task InvalidateFromAsync(int year, int monthIndex)
    {
        await _context.CycleBalances
            .Where(b => b.Year > year || (b.Year == year && b.MonthIndex >= monthIndex))
            .ExecuteDeleteAsync();
    }

    // Deletes every cached snapshot outright -- call when the CycleDay setting changes, since
    // that shifts every cycle's date boundaries retroactively and invalidates the whole history.
    public async Task InvalidateAllAsync()
    {
        await _context.CycleBalances.ExecuteDeleteAsync();
    }
}
