using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Accounts;

public sealed record LedgerAccountBalanceSnapshot(
    IReadOnlyDictionary<string, decimal> Current,
    IReadOnlyDictionary<string, decimal> ThroughExclusive);

/// <summary>
/// Reads account balances. Accounts are a placement of the four existing bucket totals, so this
/// service owns no invalidation rule of its own: it resumes from the per-cycle snapshot
/// <see cref="CycleBalanceService"/> already caches and replays only the transactions after that
/// boundary. When no usable snapshot exists it falls back to the full-history scan, which remains
/// the definition of a correct answer.
/// </summary>
public sealed class LedgerAccountBalanceService
{
    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly FinancialClock _clock;

    // Resolved at most once per request (the service is scoped). A single /api/bootstrap asks for
    // balances more than once, and the cycle day cannot change underneath a read-only request.
    private int? _cycleDay;

    public LedgerAccountBalanceService(
        AppDbContext context,
        CycleBalanceService? cycleBalanceService = null,
        FinancialClock? clock = null)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService ?? new CycleBalanceService(context);
        _clock = clock ?? FinancialClock.Utc;
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetBalancesAsync(
        IReadOnlyCollection<LedgerAccount>? accounts = null,
        CancellationToken cancellationToken = default,
        DateTime? throughExclusive = null)
    {
        var accountRows = accounts is null
            ? await _context.LedgerAccounts.AsNoTracking().ToListAsync(cancellationToken)
            : accounts.ToList();
        if (accountRows.Count == 0) return EmptyBalances(accountRows);

        // Placement is decided against the set the caller supplied, exactly as before this service
        // learned to resume from a snapshot -- re-reading the table here would both cost a query
        // and quietly change the answer for a caller that passed a subset.
        var accountsById = accountRows.ToDictionary(account => account.Id, StringComparer.Ordinal);
        var cycleDay = await GetCycleDayAsync(cancellationToken);
        var (anchorYear, anchorMonth) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            _clock.Today,
            cycleDay);

        // Everything strictly before the anchor cycle is already summed in the snapshot, so only
        // the anchor cycle onwards (including any future-dated rows) has to be replayed.
        var resumed = await ResumeFromSnapshotAsync(anchorYear, anchorMonth, cycleDay, cancellationToken);

        // A cutoff at or before the anchor cycle needs rows the snapshot has already folded in, so
        // a partial replay could not answer it -- fall back to the scan.
        if (resumed is null || (throughExclusive.HasValue && throughExclusive.Value <= resumed.Value.ReplayFrom))
        {
            return Project(await ScanAsync(accountsById, throughExclusive, cancellationToken), accountRows);
        }

        var (balances, replayFrom) = resumed.Value;
        await foreach (var transaction in TransactionsFrom(replayFrom, throughExclusive)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            LedgerAccountBalanceMath.Accumulate(balances, transaction.ToTransaction(), accountsById);
        }

        return Project(balances, accountRows);
    }

    public async Task<LedgerAccountBalanceSnapshot> GetBalanceSnapshotAsync(
        IReadOnlyCollection<LedgerAccount> accounts,
        DateTime throughExclusive,
        CancellationToken cancellationToken = default,
        int? cycleDay = null)
    {
        var accountRows = accounts.ToList();
        if (accountRows.Count == 0)
        {
            var empty = EmptyBalances(accountRows);
            return new LedgerAccountBalanceSnapshot(empty, empty);
        }

        var accountsById = accountRows.ToDictionary(account => account.Id, StringComparer.Ordinal);
        var resolvedCycleDay = cycleDay ?? await GetCycleDayAsync(cancellationToken);
        var (anchorYear, anchorMonth) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            _clock.Today,
            resolvedCycleDay);
        var resumed = await ResumeFromSnapshotAsync(
            anchorYear,
            anchorMonth,
            resolvedCycleDay,
            cancellationToken);

        // Both answers share one opening snapshot and one pass; they differ only in whether a row
        // falls before the cutoff. Without a usable snapshot the same single pass runs over the
        // whole history instead, which is the original behavior.
        var usableSnapshot = resumed is not null && throughExclusive > resumed.Value.ReplayFrom;
        var opening = usableSnapshot
            ? resumed!.Value.Balances
            : new Dictionary<string, decimal>(StringComparer.Ordinal);
        var replayFrom = usableSnapshot ? resumed!.Value.ReplayFrom : (DateTime?)null;

        var current = new Dictionary<string, decimal>(opening, StringComparer.Ordinal);
        var through = new Dictionary<string, decimal>(opening, StringComparer.Ordinal);
        await foreach (var projection in TransactionsFrom(replayFrom)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            var transaction = projection.ToTransaction();
            LedgerAccountBalanceMath.Accumulate(current, transaction, accountsById);
            if (projection.Date < throughExclusive)
                LedgerAccountBalanceMath.Accumulate(through, transaction, accountsById);
        }

        return new LedgerAccountBalanceSnapshot(
            Project(current, accountRows),
            Project(through, accountRows));
    }

    /// <summary>
    /// Returns the cumulative balances entering (<paramref name="year"/>, <paramref name="monthIndex"/>)
    /// together with the date replay must resume from, or null when no usable snapshot exists.
    /// </summary>
    private async Task<(Dictionary<string, decimal> Balances, DateTime ReplayFrom)?> ResumeFromSnapshotAsync(
        int year,
        int monthIndex,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        var opening = await _cycleBalanceService.GetOpeningAccountBalancesAsync(
            year,
            monthIndex,
            cycleDay,
            cancellationToken);
        if (opening is null) return null;

        var range = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        var replayFrom = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
        return (new Dictionary<string, decimal>(opening, StringComparer.Ordinal), replayFrom);
    }

    private async Task<Dictionary<string, decimal>> ScanAsync(
        IReadOnlyDictionary<string, LedgerAccount> accountsById,
        DateTime? throughExclusive,
        CancellationToken cancellationToken)
    {
        var balances = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var query = TransactionsFrom(null, throughExclusive);

        await foreach (var transaction in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            LedgerAccountBalanceMath.Accumulate(balances, transaction.ToTransaction(), accountsById);
        }
        return balances;
    }

    // Both bounds are applied before the projection: EF cannot translate a predicate written
    // against the projected record.
    private IQueryable<LedgerTransactionProjection> TransactionsFrom(
        DateTime? fromInclusive,
        DateTime? toExclusive = null)
    {
        var query = _context.Transactions.AsNoTracking().AsQueryable();
        if (fromInclusive.HasValue)
        {
            query = query.Where(transaction => transaction.Date >= fromInclusive.Value);
        }
        if (toExclusive.HasValue)
        {
            query = query.Where(transaction => transaction.Date < toExclusive.Value);
        }

        return query.Select(transaction => new LedgerTransactionProjection(
            transaction.Date,
            transaction.Category,
            transaction.LedgerCategory,
            transaction.Amount,
            transaction.AccountId,
            transaction.CounterAccountId));
    }

    private async Task<int> GetCycleDayAsync(CancellationToken cancellationToken) =>
        _cycleDay ??=
            (await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken))?.CycleDay
            ?? FinancialConstants.DefaultCycleDay;

    /// <summary>
    /// Restores the caller's exact key set. Accumulate only creates entries for accounts that were
    /// actually touched, and a cached snapshot can carry accounts the caller did not ask about, so
    /// the result is projected back onto the requested rows -- every one present, zero if unmoved.
    /// </summary>
    private static Dictionary<string, decimal> Project(
        IReadOnlyDictionary<string, decimal> balances,
        IEnumerable<LedgerAccount> accounts) =>
        accounts.ToDictionary(
            account => account.Id,
            account => balances.GetValueOrDefault(account.Id),
            StringComparer.Ordinal);

    private static Dictionary<string, decimal> EmptyBalances(IEnumerable<LedgerAccount> accounts) =>
        accounts.ToDictionary(account => account.Id, _ => 0m, StringComparer.Ordinal);

    private sealed record LedgerTransactionProjection(
        DateTime Date,
        string Category,
        string LedgerCategory,
        decimal Amount,
        string? AccountId,
        string? CounterAccountId)
    {
        public Transaction ToTransaction() => new()
        {
            Category = Category,
            LedgerCategory = LedgerCategory,
            Amount = Amount,
            AccountId = AccountId,
            CounterAccountId = CounterAccountId,
        };
    }
}
