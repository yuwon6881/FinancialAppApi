using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

// Phase 3: the transaction data-access seam. All Postgres reads for AI context loading go
// through these helpers so the orchestration in AiAssistantService stays free of raw EF query
// blocks. Exact figures (COUNT / SUM) are always computed by the database over the full match
// set -- never derived from the bounded in-memory row sample -- so a truncated detail sample
// can never masquerade as an exact aggregate.
public partial class AiAssistantService
{
    private async Task<Models.FinancialSetting?> LoadFinancialSettingAsync(CancellationToken cancellationToken) =>
        await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    internal sealed record AiRecurringRow(
        string Id, string Name, decimal Amount, string Category, string LedgerCategory,
        string StartDate, string? EndDate, int DueDate, bool Active,
        string Frequency, string NextDueDate, bool PushReminderEnabled = false,
        string PushReminderMode = "Once", int PushReminderLeadDays = 1,
        string PaymentMode = Models.RecurringPaymentMode.Manual,
        string? LinkedLoanId = null, string? LinkedLoanName = null);

    private sealed record AiLedgerEditMatch(string Id, string Description, DateTime Date);

    private async Task<List<AiLedgerEditMatch>> FindLedgerEditMatchesAsync(
        string? searchText,
        IReadOnlyList<string> transactionIds,
        DateTime? start,
        DateTime? end,
        CancellationToken cancellationToken)
    {
        var query = _context.Transactions
            .AsNoTracking()
            .Where(t => t.LedgerCategory != "Discarded")
            .AsQueryable();
        if (start.HasValue) query = query.Where(t => t.Date >= start.Value);
        if (end.HasValue) query = query.Where(t => t.Date < end.Value);
        if (transactionIds.Count > 0) query = query.Where(t => transactionIds.Contains(t.Id));
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var normalized = searchText.ToLowerInvariant();
            query = query.Where(t => t.Description.ToLower().Contains(normalized) ||
                t.Category.ToLower().Contains(normalized) ||
                t.LedgerCategory.ToLower().Contains(normalized));
        }
        return await query
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Select(t => new AiLedgerEditMatch(t.Id, t.Description, t.Date))
            .Take(4)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<AiRecurringRow>> LoadRecurringRowsAsync(CancellationToken cancellationToken) =>
        await (from r in _context.RecurringPayments.AsNoTracking()
            join loan in _context.Loans.AsNoTracking() on r.Id equals loan.RecurringPaymentId into linkedLoans
            from linkedLoan in linkedLoans.DefaultIfEmpty()
            orderby r.Name
            select new AiRecurringRow(
                r.Id, r.Name, r.Amount, r.Category, r.LedgerCategory,
                r.StartDate, r.EndDate, r.DueDate, r.Active,
                r.Frequency, r.NextDueDate ?? string.Empty, r.PushReminderEnabled,
                r.PushReminderMode, r.PushReminderLeadDays, r.PaymentMode,
                linkedLoan == null ? null : linkedLoan.Id,
                linkedLoan == null ? null : linkedLoan.Name))
            .Take(100)
            .ToListAsync(cancellationToken);

    // Amount stays the scheduled figure so "how much is this bill" keeps its answer; Outstanding is
    // what is still owed, and is the only one of the two a forecast may add up. They differ exactly
    // when a bill is partially paid, where summing Amount projected money already spent a second time.
    private sealed record AiRecurringStatusRow(
        string Id, string Name, string Category, string LedgerCategory, string DueDate, string Status,
        decimal? Amount, decimal Outstanding);

    // Per-cycle bill status (Paid / Pending / Discarded) for each active recurring payment whose
    // billing date lands in the requested cycle(s). Mirrors FinancialService.BuildActiveRecurringList
    // exactly -- crucially it queries transactions INCLUDING LedgerCategory=="Discarded" (which every
    // other AI loader deliberately excludes), because a discarded bill is precisely a recurring
    // payment whose generated transaction was marked Discarded. Without this the assistant could
    // never answer "which subscription was discarded this cycle".
    private async Task<List<AiRecurringStatusRow>> LoadRecurringBillStatusesAsync(
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        var recurring = await _context.RecurringPayments.AsNoTracking().Where(payment => payment.Active).ToListAsync(cancellationToken);
        var parentAmounts = recurring.ToDictionary(payment => payment.Id, payment => payment.Amount, StringComparer.Ordinal);
        var results = new List<AiRecurringStatusRow>();
        foreach (var cycle in cycles.Distinct())
        {
            var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
            var startOnly = DateOnly.FromDateTime(range.start);
            var endOnly = DateOnly.FromDateTime(range.end);
            var occurrences = await _recurringOccurrenceLedger.GetRangeAsync(
                recurring, startOnly, endOnly, cancellationToken: cancellationToken);

            // Only partially paid rows need their ledger transactions read back; every other status
            // either owes its full scheduled amount or owes nothing.
            var partiallyPaid = occurrences
                .Where(occurrence => occurrence.Status == RecurringOccurrenceStatus.PartiallyPaid)
                .ToList();
            Dictionary<(string PaymentId, DateOnly Date), decimal>? paidByOccurrence = null;
            if (partiallyPaid.Count > 0)
            {
                var partialIds = partiallyPaid.Select(item => item.RecurringPaymentId).Distinct(StringComparer.Ordinal).ToList();
                var partialDates = partiallyPaid.Select(item => item.OccurrenceDate).Distinct().ToList();
                var partialTransactions = await _context.Transactions
                    .AsNoTracking()
                    .Where(transaction => transaction.RecurringPaymentId != null
                        && partialIds.Contains(transaction.RecurringPaymentId)
                        && transaction.RecurringOccurrenceDate != null
                        && partialDates.Contains(transaction.RecurringOccurrenceDate.Value))
                    .ToListAsync(cancellationToken);
                paidByOccurrence = RecurringOccurrenceAmounts.PaidByOccurrence(partialTransactions);
            }

            foreach (var occurrence in occurrences)
            {
                results.Add(new AiRecurringStatusRow(
                    occurrence.RecurringPaymentId,
                    occurrence.Name,
                    occurrence.Category ?? string.Empty,
                    occurrence.LedgerCategory ?? string.Empty,
                    occurrence.OccurrenceDate.ToString("yyyy-MM-dd"),
                    occurrence.Status,
                    occurrence.ScheduledAmount.HasValue ? Math.Abs(occurrence.ScheduledAmount.Value) : null,
                    RecurringOccurrenceAmounts.Outstanding(
                        occurrence,
                        parentAmounts.GetValueOrDefault(occurrence.RecurringPaymentId),
                        paidByOccurrence)));
            }
        }
        return results;
    }

    private async Task<List<AiWishlistRow>> LoadWishlistRowsAsync(
        int? itemId,
        CancellationToken cancellationToken)
    {
        var query = _context.WishlistItems
            .AsNoTracking()
            .OrderBy(w => w.IsPurchased)
            .ThenByDescending(w => w.IsActive)
            .ThenByDescending(w => w.CreatedAt)
            .AsQueryable();
        if (itemId is > 0) query = query.Where(w => w.Id == itemId);
        return await query
            .Select(w => new AiWishlistRow(w.Id, w.Name, w.Price, w.Priority, w.IsActive, w.IsPurchased, w.CreatedAt, w.PurchasedAt))
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    // Unfiltered (no search text) transactions across the given cycles, used for ledger-balance
    // and forecast math. The primary load may be narrowed to a merchant/activity search, which
    // must never restrict a balance/savings-rate computation -- those need every row in the cycle.
    private async Task<List<AiTransactionRow>> LoadUnfilteredCycleTransactionsAsync(
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        var result = new List<AiTransactionRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var range in MergeCycleRanges(cycles, cycleDay))
        {
            var rows = await QueryTransactionsAsync(range.Start, range.End, null, null, cancellationToken);
            if (rows.Count > MaxTransactionsPerRange) rows = rows.Take(MaxTransactionsPerRange).ToList();
            foreach (var row in rows)
            {
                if (seen.Add(row.Id)) result.Add(row);
            }
        }
        return result;
    }

    private async Task<List<AiTransactionRow>> LoadRecentFallbackTransactionsAsync(
        string? searchText,
        IReadOnlyList<string> transactionIds,
        CancellationToken cancellationToken)
    {
        var query = _context.Transactions
            .AsNoTracking()
            .Where(t => t.LedgerCategory != "Discarded")
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var normalized = searchText.Trim().ToLowerInvariant();
            query = query.Where(t => t.Description.ToLower().Contains(normalized) ||
                t.Category.ToLower().Contains(normalized) ||
                t.LedgerCategory.ToLower().Contains(normalized));
        }
        if (transactionIds.Count > 0) query = query.Where(t => transactionIds.Contains(t.Id));
        var rows = await query
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Take(500)
            .Select(t => new AiTransactionDbRow(t.Id, t.Date, t.PostedAt, t.Description, t.Category, t.LedgerCategory, t.Amount, t.RecurringPaymentId, t.AccountId, t.CounterAccountId))
            .ToListAsync(cancellationToken);
        return rows.Select(ToAiTransactionRow).ToList();
    }

    // Applies the shared "not discarded, within [start,end), optional text match" predicate.
    private IQueryable<Models.Transaction> ScopedTransactions(DateTime start, DateTime end, string? searchText, IReadOnlyList<string>? transactionIds = null)
    {
        var query = _context.Transactions
            .AsNoTracking()
            .Where(t => t.LedgerCategory != "Discarded" && t.Date >= start && t.Date < end);
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var normalized = searchText.Trim().ToLowerInvariant();
            query = query.Where(t => t.Description.ToLower().Contains(normalized) ||
                t.Category.ToLower().Contains(normalized) ||
                t.LedgerCategory.ToLower().Contains(normalized));
        }
        if (transactionIds is { Count: > 0 })
        {
            query = query.Where(t => transactionIds.Contains(t.Id));
        }
        return query;
    }

    private async Task<List<AiTransactionRow>> QueryTransactionsAsync(
        DateTime start,
        DateTime end,
        string? searchText,
        IReadOnlyList<string>? transactionIds,
        CancellationToken cancellationToken)
        {
        var rows = await ScopedTransactions(start, end, searchText, transactionIds)
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            // One beyond the cap so the caller can tell "exactly at the cap" (complete) apart
            // from "over the cap" (truncated); the caller drops the sentinel row.
            .Take(MaxTransactionsPerRange + 1)
            .Select(t => new AiTransactionDbRow(t.Id, t.Date, t.PostedAt, t.Description, t.Category, t.LedgerCategory, t.Amount, t.RecurringPaymentId, t.AccountId, t.CounterAccountId))
            .ToListAsync(cancellationToken);
        return rows.Select(ToAiTransactionRow).ToList();
    }

    // Exact match count over the full result set (not the bounded sample).
    private async Task<int> CountTransactionsAsync(
        DateTime start,
        DateTime end,
        string searchText,
        IReadOnlyList<string>? transactionIds,
        CancellationToken cancellationToken) =>
        await ScopedTransactions(start, end, searchText, transactionIds).CountAsync(cancellationToken);

    // Exact reportable outflow total over the full result set. The recovery loop uses this to
    // replace a truncated approximate cycle total with a precise SUM computed in the database;
    // keep the SQL predicates aligned with TransactionReportSemantics, including adjustments and
    // discarded markers that must not affect report cash flow.
    private async Task<decimal> SumOutflowAsync(
        DateTime start,
        DateTime end,
        string? searchText,
        IReadOnlyList<string>? transactionIds,
        CancellationToken cancellationToken)
    {
        var total = await ScopedTransactions(start, end, searchText, transactionIds)
            .Where(t => t.Amount < 0 &&
                t.Category.ToLower() != "transfer" &&
                t.Category.ToLower() != "adjustment" &&
                t.LedgerCategory.ToLower() != "discarded" &&
                !t.LedgerCategory.ToLower().StartsWith("transfer:"))
            .SumAsync(t => (decimal?)t.Amount, cancellationToken);
        return Math.Abs(total ?? 0m);
    }

    private static AiTransactionRow ToAiTransactionRow(AiTransactionDbRow row) => new(
        row.Id,
        row.Date,
        TransactionDate.ToDateOnly(row.Date).ToString("yyyy-MM-dd"),
        row.Description,
        row.Category,
        row.LedgerCategory,
        row.Amount,
        row.PostedAt,
        row.RecurringPaymentId,
        row.AccountId,
        row.CounterAccountId);
}
