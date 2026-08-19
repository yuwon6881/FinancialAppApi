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

    // Parent existence is independent of Active: paused payments still own their pending rows.
    // Cache the lookup so one service request performs at most one batched orphan check.
    private readonly Dictionary<string, bool> _paymentExists = new();
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

        var occurrences = _known.Values.Where(occurrence =>
            occurrence.OccurrenceDate >= start && occurrence.OccurrenceDate <= end).ToList();
        occurrences = await DropOrphanedPendingAsync(
            occurrences,
            activePayments.Select(payment => payment.Id).ToHashSet(StringComparer.Ordinal),
            cancellationToken);
        return Ordered(occurrences);
    }

    public async Task<List<RecurringPaymentOccurrence>> GetPendingDueAsync(
        IReadOnlyCollection<RecurringPayment> activePayments,
        CancellationToken cancellationToken = default)
    {
        var today = _clock.Today;
        var active = activePayments.Where(payment => payment.Active).ToList();
        if (active.Count == 0) return new List<RecurringPaymentOccurrence>();

        var activeIds = active.Select(payment => payment.Id).ToHashSet(StringComparer.Ordinal);
        var endDates = active.ToDictionary(
            payment => payment.Id,
            payment => DateOnly.TryParseExact(payment.EndDate, "yyyy-MM-dd", out var end)
                ? end
                : (DateOnly?)null,
            StringComparer.Ordinal);
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

        var occurrences = _known.Values.Where(occurrence =>
            activeIds.Contains(occurrence.RecurringPaymentId)
            && RecurringOccurrenceStatus.IsUnresolved(occurrence.Status)
            && occurrence.OccurrenceDate <= today
            && (endDates[occurrence.RecurringPaymentId] is not { } end
                || today <= end)).ToList();
        occurrences = await DropOrphanedPendingAsync(
            occurrences,
            activeIds,
            cancellationToken);
        return Ordered(occurrences);
    }

    // Pending/PartiallyPaid rows are only commitments while their parent exists. Paid and
    // discarded rows are settled history and deliberately survive deletion of the template.
    private async Task<List<RecurringPaymentOccurrence>> DropOrphanedPendingAsync(
        List<RecurringPaymentOccurrence> occurrences,
        IReadOnlyCollection<string> knownExistingIds,
        CancellationToken cancellationToken)
    {
        var candidateIds = occurrences
            .Where(occurrence => RecurringOccurrenceStatus.IsUnresolved(occurrence.Status)
                && !knownExistingIds.Contains(occurrence.RecurringPaymentId)
                && !_paymentExists.ContainsKey(occurrence.RecurringPaymentId))
            .Select(occurrence => occurrence.RecurringPaymentId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidateIds.Count > 0)
        {
            var existingIds = (await _context.RecurringPayments
                    .AsNoTracking()
                    .Where(payment => candidateIds.Contains(payment.Id))
                    .Select(payment => payment.Id)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var id in candidateIds)
            {
                _paymentExists[id] = existingIds.Contains(id);
            }
        }
        return occurrences.Where(occurrence =>
            !RecurringOccurrenceStatus.IsUnresolved(occurrence.Status)
            || knownExistingIds.Contains(occurrence.RecurringPaymentId)
            || _paymentExists.GetValueOrDefault(occurrence.RecurringPaymentId, true)).ToList();
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
            if (RecurringOccurrenceStatus.IsUnresolved(occurrence.Status))
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
                if (RecurringOccurrenceStatus.IsUnresolved(occurrence.Status))
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
                // Pending only, deliberately: a future PartiallyPaid row has ledger transactions
                // against it, so dropping it on a schedule edit would orphan real money. It survives
                // on its own snapshot, and the partial-payment guards stop the bill being paused or
                // deleted while it is still open.
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

    public static void SettleFromTransaction(
        RecurringPaymentOccurrence occurrence,
        Transaction transaction,
        decimal totalPaidForOccurrence = 0m)
    {
        var discarded = string.Equals(transaction.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase);
        if (discarded)
        {
            occurrence.Status = RecurringOccurrenceStatus.Discarded;
            occurrence.PaidDate = null;
            return;
        }

        var scheduled = occurrence.ScheduledAmount.GetValueOrDefault();
        var paid = totalPaidForOccurrence > 0m ? totalPaidForOccurrence : Math.Abs(transaction.Amount);
        if (scheduled > 0m && paid < scheduled)
        {
            occurrence.Status = RecurringOccurrenceStatus.PartiallyPaid;
            occurrence.PaidDate = null;
        }
        else
        {
            occurrence.Status = RecurringOccurrenceStatus.Paid;
            occurrence.PaidDate = TransactionDate.ToDateOnly(transaction.Date);
        }
    }

    public static void RecomputeOccurrenceStatus(
        RecurringPaymentOccurrence occurrence,
        IReadOnlyCollection<Transaction> transactions)
    {
        if (occurrence.Status == RecurringOccurrenceStatus.SettledByLoanPayoff)
            return;

        var hasDiscarded = transactions.Any(t =>
            t.Amount == 0m && string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase));
        if (hasDiscarded)
        {
            occurrence.Status = RecurringOccurrenceStatus.Discarded;
            occurrence.PaidDate = null;
            return;
        }

        var activeTxs = transactions.Where(t =>
            !string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase)).ToList();
        var totalPaid = activeTxs.Sum(t => Math.Abs(t.Amount));
        var scheduled = occurrence.ScheduledAmount.GetValueOrDefault();

        // The >= comparison only means anything with a scheduled amount to compare against. With
        // none, any money at all settles the occurrence — matching SettleFromTransaction, which took
        // that branch already. Treated as "not yet enough" the row became PartiallyPaid with no
        // reachable path to Paid, and partial rows block pausing and deleting the bill.
        if (totalPaid > 0m && (scheduled <= 0m || totalPaid >= scheduled))
        {
            occurrence.Status = RecurringOccurrenceStatus.Paid;
            occurrence.PaidDate = activeTxs.Count > 0
                ? activeTxs.Max(t => (DateOnly?)TransactionDate.ToDateOnly(t.Date))
                : null;
        }
        else if (totalPaid > 0m)
        {
            occurrence.Status = RecurringOccurrenceStatus.PartiallyPaid;
            occurrence.PaidDate = null;
        }
        else
        {
            occurrence.Status = RecurringOccurrenceStatus.Pending;
            occurrence.PaidDate = null;
        }
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

        var groups = transactions
            .GroupBy(t => (PaymentId: t.RecurringPaymentId!, Date: t.RecurringOccurrenceDate!.Value));

        foreach (var group in groups)
        {
            var (paymentId, date) = group.Key;
            var groupTxs = group.ToList();
            var hasDiscarded = groupTxs.Any(t => t.Amount == 0m &&
                string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase));
            var activeTxs = groupTxs.Where(t =>
                !string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase)).ToList();
            var totalPaid = activeTxs.Sum(t => Math.Abs(t.Amount));
            var lastPaymentDate = activeTxs.Count > 0 ? activeTxs.Max(t => (DateOnly?)TransactionDate.ToDateOnly(t.Date)) : null;

            if (_known.TryGetValue((paymentId, date), out var tracked))
            {
                if (tracked.Status is not (RecurringOccurrenceStatus.Discarded or RecurringOccurrenceStatus.SettledByLoanPayoff))
                {
                    var scheduled = tracked.ScheduledAmount.GetValueOrDefault();
                    if (hasDiscarded)
                    {
                        tracked.Status = RecurringOccurrenceStatus.Discarded;
                        tracked.PaidDate = null;
                    }
                    else if (scheduled > 0m && totalPaid >= scheduled)
                    {
                        tracked.Status = RecurringOccurrenceStatus.Paid;
                        tracked.PaidDate = lastPaymentDate;
                    }
                    else if (totalPaid > 0m)
                    {
                        tracked.Status = RecurringOccurrenceStatus.PartiallyPaid;
                        tracked.PaidDate = null;
                    }
                    else
                    {
                        tracked.Status = RecurringOccurrenceStatus.Pending;
                        tracked.PaidDate = null;
                    }
                }
                continue;
            }

            var firstTx = activeTxs.FirstOrDefault() ?? groupTxs.First();
            var scheduledAmount = hasDiscarded ? null : (decimal?)Math.Abs(firstTx.Amount);
            var status = hasDiscarded
                ? RecurringOccurrenceStatus.Discarded
                : (scheduledAmount.HasValue && totalPaid >= scheduledAmount.Value && totalPaid > 0m)
                    ? RecurringOccurrenceStatus.Paid
                    : totalPaid > 0m
                        ? RecurringOccurrenceStatus.PartiallyPaid
                        : RecurringOccurrenceStatus.Pending;

            var backfilled = new RecurringPaymentOccurrence
            {
                Id = $"occ-{paymentId}-{date:yyyyMMdd}",
                UserId = firstTx.UserId,
                RecurringPaymentId = paymentId,
                OccurrenceDate = date,
                Name = firstTx.Description,
                ScheduledAmount = scheduledAmount,
                Category = firstTx.Category,
                LedgerCategory = hasDiscarded ? null : firstTx.LedgerCategory,
                AccountId = hasDiscarded ? null : firstTx.AccountId,
                PaymentMode = modes.GetValueOrDefault(paymentId) ?? RecurringPaymentMode.Manual,
                Status = status,
                PaidDate = status == RecurringOccurrenceStatus.Paid ? lastPaymentDate : null
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
