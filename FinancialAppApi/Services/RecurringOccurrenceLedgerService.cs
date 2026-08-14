using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed class RecurringOccurrenceLedgerService
{
    private const int MaxOccurrencesToScan = 60;
    private readonly AppDbContext _context;
    private readonly RecurringOccurrenceService _dates;
    private readonly FinancialClock _clock;

    // Occurrences this service has already read or Added, keyed by their natural key. Two
    // things depend on it. Materialising a range used to cost one lookup per date, which put
    // an N+1 on /api/bootstrap for every tracked bill. And a lookup issued against the store
    // cannot see a row that is Added but not yet saved, so the backfill's inserts were
    // invisible to the materialiser, which then Added a second entity under the same
    // deterministic `occ-{payment}-{date}` key and threw an identity conflict.
    private readonly Dictionary<(string PaymentId, DateOnly Date), RecurringPaymentOccurrence> _known = new();

    // Ranges already loaded in full, so a miss inside one is proof of absence rather than a
    // reason to ask the database again. A null payment id means the range was loaded for
    // every payment.
    private readonly List<(string? PaymentId, DateOnly Start, DateOnly End)> _loaded = new();

    public RecurringOccurrenceLedgerService(
        AppDbContext context,
        RecurringOccurrenceService dates,
        FinancialClock? clock = null)
    {
        _context = context;
        _dates = dates;
        _clock = clock ?? FinancialClock.Utc;
    }

    // `knownTransactions` is any set the caller has already loaded that covers this range.
    // The bootstrap snapshot reads exactly these rows a moment earlier, so handing them over
    // keeps the backfill from re-scanning the ledger on every cold start and post-drain
    // refresh.
    public async Task<List<RecurringPaymentOccurrence>> GetRangeAsync(
        IReadOnlyCollection<RecurringPayment> activePayments,
        DateOnly start,
        DateOnly end,
        IReadOnlyCollection<Transaction>? knownTransactions = null,
        CancellationToken cancellationToken = default)
    {
        await LoadRangeAsync(paymentId: null, start, end, cancellationToken);
        await BackfillTaggedTransactionsAsync(activePayments, knownTransactions, start, end, cancellationToken);
        foreach (var payment in activePayments.Where(payment => payment.Active))
        {
            EnsureRange(payment, start, end);
        }
        await SaveIfChangedAsync(cancellationToken);

        return Ordered(_known.Values.Where(occurrence =>
            occurrence.OccurrenceDate >= start && occurrence.OccurrenceDate <= end));
    }

    public async Task<List<RecurringPaymentOccurrence>> GetPendingDueAsync(
        IReadOnlyCollection<RecurringPayment> activePayments,
        CancellationToken cancellationToken = default)
    {
        var today = _clock.Today;
        var active = activePayments.Where(payment => payment.Active).ToList();
        if (active.Count == 0) return new List<RecurringPaymentOccurrence>();

        var activeIds = active.Select(payment => payment.Id).ToList();
        var earliest = active.Min(TrackingStart);
        await LoadRangeAsync(activeIds, earliest, today, cancellationToken);
        var invented = false;
        foreach (var payment in active)
        {
            invented |= EnsureRange(payment, TrackingStart(payment), today);
        }
        // Only a call that actually invented pending rows can have invented one that a tagged
        // settlement already answers -- a bill paid early for a later cycle, say. Reconciling
        // then costs one scan the first time a bill is materialised; in steady state nothing is
        // created and the dashboard pays nothing. Skipping it would persist that row as pending
        // and go on nagging about a bill the user has already paid.
        if (invented)
        {
            await BackfillTaggedTransactionsAsync(active, null, earliest, today, cancellationToken);
        }
        await SaveIfChangedAsync(cancellationToken);

        return Ordered(_known.Values.Where(occurrence =>
            activeIds.Contains(occurrence.RecurringPaymentId)
            && occurrence.Status == RecurringOccurrenceStatus.Pending
            && occurrence.OccurrenceDate <= today));
    }

    private static List<RecurringPaymentOccurrence> Ordered(IEnumerable<RecurringPaymentOccurrence> occurrences) =>
        occurrences
            .OrderBy(occurrence => occurrence.OccurrenceDate)
            .ThenBy(occurrence => occurrence.Id, StringComparer.Ordinal)
            .ToList();

    private async Task SaveIfChangedAsync(CancellationToken cancellationToken)
    {
        if (_context.ChangeTracker.HasChanges())
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<RecurringPaymentOccurrence?> GetNextPendingAsync(
        RecurringPayment payment,
        DateOnly from,
        bool includeFrom,
        CancellationToken cancellationToken = default)
    {
        var cursor = includeFrom ? from : from.AddDays(1);
        var trackingStart = TrackingStart(payment);
        if (cursor < trackingStart) cursor = trackingStart;

        for (var i = 0; i < MaxOccurrencesToScan; i++)
        {
            var date = _dates.GetNextOccurrenceOnOrAfter(payment, cursor);
            if (date == null) return null;
            var occurrence = await EnsureOccurrenceAsync(payment, date.Value, cancellationToken);
            if (occurrence.Status == RecurringOccurrenceStatus.Pending)
            {
                await _context.SaveChangesAsync(cancellationToken);
                return occurrence;
            }
            cursor = date.Value.AddDays(1);
        }

        return null;
    }

    public async Task<Dictionary<string, RecurringPaymentOccurrence>> GetNextPendingAsync(
        IReadOnlyCollection<RecurringPayment> payments,
        DateOnly from,
        bool includeFrom,
        CancellationToken cancellationToken = default)
    {
        var active = payments.Where(payment => payment.Active).ToList();
        if (active.Count == 0) return [];

        var firstCursor = includeFrom ? from : from.AddDays(1);
        var earliest = active.Min(payment => firstCursor < TrackingStart(payment)
            ? TrackingStart(payment)
            : firstCursor);
        var ids = active.Select(payment => payment.Id).ToList();
        foreach (var occurrence in await _context.RecurringPaymentOccurrences
                     .Where(occurrence => ids.Contains(occurrence.RecurringPaymentId)
                         && occurrence.OccurrenceDate >= earliest)
                     .ToListAsync(cancellationToken))
        {
            Remember(occurrence);
        }
        foreach (var payment in active)
        {
            var cursor = firstCursor < TrackingStart(payment) ? TrackingStart(payment) : firstCursor;
            _loaded.Add((payment.Id, cursor, DateOnly.MaxValue));
        }

        var result = new Dictionary<string, RecurringPaymentOccurrence>(active.Count);
        foreach (var payment in active)
        {
            var cursor = firstCursor < TrackingStart(payment) ? TrackingStart(payment) : firstCursor;
            for (var index = 0; index < MaxOccurrencesToScan; index++)
            {
                var date = _dates.GetNextOccurrenceOnOrAfter(payment, cursor);
                if (date == null) break;
                var key = (payment.Id, date.Value);
                var occurrence = _known.TryGetValue(key, out var known)
                    ? known
                    : Materialize(payment, date.Value);
                if (occurrence.Status == RecurringOccurrenceStatus.Pending)
                {
                    result[payment.Id] = occurrence;
                    break;
                }
                cursor = date.Value.AddDays(1);
            }
        }
        await SaveIfChangedAsync(cancellationToken);
        return result;
    }

    public async Task PreserveThroughTodayAndResetFutureAsync(
        RecurringPayment payment,
        bool preserveThroughToday = true,
        CancellationToken cancellationToken = default)
    {
        var today = _clock.Today;
        if (preserveThroughToday)
        {
            await LoadRangeAsync(payment.Id, TrackingStart(payment), today, cancellationToken);
            EnsureRange(payment, TrackingStart(payment), today);
        }
        var futurePending = await _context.RecurringPaymentOccurrences
            .Where(occurrence => occurrence.RecurringPaymentId == payment.Id
                && occurrence.OccurrenceDate > today
                && occurrence.Status == RecurringOccurrenceStatus.Pending)
            .ToListAsync(cancellationToken);
        _context.RecurringPaymentOccurrences.RemoveRange(futurePending);
        // These rows are gone; leaving them in the lookup would hand a deleted entity back to
        // the next caller in this request.
        foreach (var occurrence in futurePending)
        {
            _known.Remove((occurrence.RecurringPaymentId, occurrence.OccurrenceDate));
        }
        payment.OccurrenceTrackingStartDate = today.AddDays(1);
        payment.NextDueDate = null;
    }

    public async Task<RecurringPaymentOccurrence> EnsureOccurrenceAsync(
        RecurringPayment payment,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindAsync(payment.Id, date, cancellationToken);
        return existing ?? Materialize(payment, date);
    }

    public async Task<RecurringPaymentOccurrence?> FindByTransactionAsync(
        Transaction transaction,
        CancellationToken cancellationToken = default)
    {
        if (transaction.RecurringPaymentId == null || transaction.RecurringOccurrenceDate == null) return null;
        return await FindAsync(
            transaction.RecurringPaymentId,
            transaction.RecurringOccurrenceDate.Value,
            cancellationToken);
    }

    private async Task<RecurringPaymentOccurrence?> FindAsync(
        string paymentId,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        if (_known.TryGetValue((paymentId, date), out var known)) return known;
        if (IsLoaded(paymentId, date)) return null;

        var existing = await _context.RecurringPaymentOccurrences.FirstOrDefaultAsync(
            occurrence => occurrence.RecurringPaymentId == paymentId && occurrence.OccurrenceDate == date,
            cancellationToken);
        if (existing != null) Remember(existing);
        return existing;
    }

    private RecurringPaymentOccurrence Materialize(RecurringPayment payment, DateOnly date)
    {
        if (date < TrackingStart(payment) || _dates.GetNextOccurrenceOnOrAfter(payment, date) != date)
        {
            throw new InvalidOperationException("The selected date is not a tracked occurrence of this recurring payment.");
        }

        var occurrence = Snapshot(payment, date);
        _context.RecurringPaymentOccurrences.Add(occurrence);
        Remember(occurrence);
        return occurrence;
    }

    private void Remember(RecurringPaymentOccurrence occurrence) =>
        _known[(occurrence.RecurringPaymentId, occurrence.OccurrenceDate)] = occurrence;

    private bool IsLoaded(string paymentId, DateOnly date) => _loaded.Any(range =>
        (range.PaymentId == null || range.PaymentId == paymentId)
        && date >= range.Start
        && date <= range.End);

    private Task LoadRangeAsync(string? paymentId, DateOnly start, DateOnly end, CancellationToken cancellationToken) =>
        LoadRangeAsync(paymentId == null ? null : new List<string> { paymentId }, start, end, cancellationToken);

    /// <summary>
    /// Reads every occurrence in a range in one query so the per-date materialisation below
    /// costs no round trips at all. A null <paramref name="paymentIds"/> loads the range for
    /// every payment.
    /// </summary>
    private async Task LoadRangeAsync(
        IReadOnlyCollection<string>? paymentIds,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        if (paymentIds != null && paymentIds.Count == 0) return;
        var outstanding = paymentIds?.Where(id => !(IsLoaded(id, start) && IsLoaded(id, end))).ToList();
        if (outstanding is { Count: 0 }) return;
        if (outstanding == null && IsLoadedForEveryPayment(start, end)) return;

        var query = _context.RecurringPaymentOccurrences
            .Where(occurrence => occurrence.OccurrenceDate >= start && occurrence.OccurrenceDate <= end);
        if (outstanding != null)
        {
            query = query.Where(occurrence => outstanding.Contains(occurrence.RecurringPaymentId));
        }

        foreach (var occurrence in await query.ToListAsync(cancellationToken))
        {
            Remember(occurrence);
        }

        if (outstanding == null)
        {
            _loaded.Add((null, start, end));
        }
        else
        {
            foreach (var id in outstanding) _loaded.Add((id, start, end));
        }
    }

    private bool IsLoadedForEveryPayment(DateOnly start, DateOnly end) => _loaded.Any(range =>
        range.PaymentId == null && start >= range.Start && end <= range.End);

    public static void SettleFromTransaction(RecurringPaymentOccurrence occurrence, Transaction transaction)
    {
        var discarded = string.Equals(transaction.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase);
        occurrence.Status = discarded ? RecurringOccurrenceStatus.Discarded : RecurringOccurrenceStatus.Paid;
        occurrence.PaidDate = discarded ? null : TransactionDate.ToDateOnly(transaction.Date);
        occurrence.SettlementTransactionId = transaction.Id;
    }

    public static void Reopen(RecurringPaymentOccurrence occurrence)
    {
        occurrence.Status = RecurringOccurrenceStatus.Pending;
        occurrence.PaidDate = null;
        occurrence.SettlementTransactionId = null;
    }

    // Synchronous by design: the range it walks is preloaded by the caller, so every date is
    // answered from `_known`. Reintroducing a query here reintroduces the N+1.
    /// <returns>True when at least one occurrence was newly materialised.</returns>
    private bool EnsureRange(RecurringPayment payment, DateOnly requestedStart, DateOnly requestedEnd)
    {
        var trackingStart = TrackingStart(payment);
        if (requestedEnd < trackingStart) return false;
        var start = requestedStart < trackingStart
            ? trackingStart
            : requestedStart;
        var cursor = start;
        var materialized = false;
        for (var i = 0; i < MaxOccurrencesToScan; i++)
        {
            var date = _dates.GetNextOccurrenceOnOrAfter(payment, cursor);
            if (date == null || date > requestedEnd) break;
            if (!_known.ContainsKey((payment.Id, date.Value)))
            {
                Materialize(payment, date.Value);
                materialized = true;
            }
            cursor = date.Value.AddDays(1);
        }
        return materialized;
    }

    private static RecurringPaymentOccurrence Snapshot(RecurringPayment payment, DateOnly date) => new()
    {
        Id = $"occ-{payment.Id}-{date:yyyyMMdd}",
        RecurringPaymentId = payment.Id,
        OccurrenceDate = date,
        Name = payment.Name,
        ScheduledAmount = Math.Abs(payment.Amount),
        Category = payment.Category,
        LedgerCategory = payment.LedgerCategory,
        AccountId = payment.AccountId,
        PaymentMode = payment.PaymentMode,
        Status = RecurringOccurrenceStatus.Pending
    };

    private async Task BackfillTaggedTransactionsAsync(
        IReadOnlyCollection<RecurringPayment> activePayments,
        IReadOnlyCollection<Transaction>? knownTransactions,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        var transactions = knownTransactions == null
            ? await _context.Transactions.AsNoTracking()
                .Where(transaction => transaction.RecurringPaymentId != null
                    && transaction.RecurringOccurrenceDate != null
                    && transaction.RecurringOccurrenceDate >= start
                    && transaction.RecurringOccurrenceDate <= end)
                .ToListAsync(cancellationToken)
            : knownTransactions
                .Where(transaction => transaction.RecurringPaymentId != null
                    && transaction.RecurringOccurrenceDate != null
                    && transaction.RecurringOccurrenceDate >= start
                    && transaction.RecurringOccurrenceDate <= end)
                .ToList();
        if (transactions.Count == 0) return;

        // The caller's active payments already answer most of these; only a settlement that
        // belongs to a payment since deactivated needs a lookup.
        var modes = activePayments.ToDictionary(payment => payment.Id, payment => payment.PaymentMode);
        var unknownIds = transactions
            .Select(transaction => transaction.RecurringPaymentId!)
            .Where(id => !modes.ContainsKey(id))
            .Distinct()
            .ToList();
        if (unknownIds.Count > 0)
        {
            var extra = await _context.RecurringPayments.AsNoTracking()
                .Where(payment => unknownIds.Contains(payment.Id))
                .ToDictionaryAsync(payment => payment.Id, payment => payment.PaymentMode, cancellationToken);
            foreach (var entry in extra) modes[entry.Key] = entry.Value;
        }

        foreach (var transaction in transactions)
        {
            var date = transaction.RecurringOccurrenceDate!.Value;
            if (_known.TryGetValue((transaction.RecurringPaymentId!, date), out var tracked))
            {
                // A tagged settlement outranks a row that was only ever projected as pending.
                // An already-settled row is left alone: it carries the wording and amount as
                // they stood when it settled, which is the point of the snapshot.
                if (tracked.Status == RecurringOccurrenceStatus.Pending)
                {
                    SettleFromTransaction(tracked, transaction);
                }
                continue;
            }
            var discarded = transaction.Amount == 0m &&
                string.Equals(transaction.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase);
            var backfilled = new RecurringPaymentOccurrence
            {
                Id = $"occ-{transaction.RecurringPaymentId}-{date:yyyyMMdd}",
                UserId = transaction.UserId,
                RecurringPaymentId = transaction.RecurringPaymentId!,
                OccurrenceDate = date,
                Name = transaction.Description,
                ScheduledAmount = discarded ? null : Math.Abs(transaction.Amount),
                Category = transaction.Category,
                LedgerCategory = discarded ? null : transaction.LedgerCategory,
                // Backfilled from what actually paid it, not from the schedule's current account.
                AccountId = discarded ? null : transaction.AccountId,
                PaymentMode = modes.GetValueOrDefault(transaction.RecurringPaymentId!) ?? RecurringPaymentMode.Manual,
                Status = discarded ? RecurringOccurrenceStatus.Discarded : RecurringOccurrenceStatus.Paid,
                PaidDate = discarded ? null : TransactionDate.ToDateOnly(transaction.Date),
                SettlementTransactionId = transaction.Id
            };
            _context.RecurringPaymentOccurrences.Add(backfilled);
            Remember(backfilled);
        }
    }

    private static DateOnly TrackingStart(RecurringPayment payment) =>
        payment.OccurrenceTrackingStartDate != default
            ? payment.OccurrenceTrackingStartDate
            : DateOnly.TryParseExact(payment.StartDate, "yyyy-MM-dd", out var start) ? start : DateOnly.MinValue;
}
