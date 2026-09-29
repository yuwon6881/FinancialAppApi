using System.Linq.Expressions;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.AI;

public sealed record AiTransactionFilter(
    IReadOnlyList<AiDateRange> Ranges,
    string? Query = null,
    // "auto" walks the exact -> spacing -> fuzzy ladder; "contains" and "whole-word" use one pass.
    string Match = "auto",
    string? Category = null,
    string? LedgerCategory = null,
    // null | "outflow" | "inflow" | "income" | "transfer"
    string? TxType = null,
    decimal? MinAmount = null,
    decimal? MaxAmount = null,
    string? AccountId = null,
    // Leaves out system-generated rows the user never typed (ExcludeFromAutocomplete), so a
    // purchase-habit question counts only real purchases.
    bool ExcludeSystemRows = false);

public sealed record AiTransactionRecord(
    string Id,
    DateOnly Date,
    DateTime PostedAt,
    string Description,
    string Category,
    string LedgerCategory,
    decimal Amount,
    string? AccountId,
    string? CounterAccountId,
    string? RecurringPaymentId);

// How the query text was matched: "exact" substring, "spacing" (separators ignored, so "hair cut"
// finds "Haircut"), "fuzzy" (close spelling), or "none" when nothing matched on any rung.
public sealed record AiTransactionMatch(IQueryable<Transaction> Query, string MatchMode);

// SQL-side transaction access for the assistant's tools. Every aggregate runs in the database
// over the full match set, so a count or total is exact no matter how few rows are returned.
public sealed class AiTransactionQueryService
{
    private readonly AppDbContext _context;

    public AiTransactionQueryService(AppDbContext context) => _context = context;

    private bool UseNpgsql => _context.Database.IsNpgsql();

    // Rows the Ledger shows: everything except discarded markers, within the ranges and filters.
    public async Task<AiTransactionMatch> MatchAsync(AiTransactionFilter filter, CancellationToken cancellationToken)
    {
        var scoped = Scoped(filter);
        if (string.IsNullOrWhiteSpace(filter.Query)) return new AiTransactionMatch(scoped, "all");

        var query = filter.Query.Trim();
        if (filter.Match is "contains" or "whole-word")
        {
            return new AiTransactionMatch(TransactionTextSearch.Apply(scoped, UseNpgsql, query, filter.Match), "exact");
        }

        // Each rung runs only when the stricter one above it found nothing, so an exact hit is
        // never diluted by a looser reading of the same words.
        var exact = TransactionTextSearch.Apply(scoped, UseNpgsql, query, "contains");
        if (await exact.AnyAsync(cancellationToken)) return new AiTransactionMatch(exact, "exact");

        if (TransactionTextSearch.CanApplyCompact(query))
        {
            var compact = TransactionTextSearch.ApplyCompact(scoped, query);
            if (await compact.AnyAsync(cancellationToken)) return new AiTransactionMatch(compact, "spacing");
        }

        var fuzzy = await FuzzyAsync(scoped, query, cancellationToken);
        return await fuzzy.AnyAsync(cancellationToken)
            ? new AiTransactionMatch(fuzzy, "fuzzy")
            : new AiTransactionMatch(fuzzy, "none");
    }

    public async Task<IReadOnlyList<AiTransactionRecord>> ListAsync(
        IQueryable<Transaction> query,
        string sort,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await Order(query, sort)
            .Take(limit)
            .Select(t => new
            {
                t.Id, t.Date, t.PostedAt, t.Description, t.Category, t.LedgerCategory, t.Amount,
                t.AccountId, t.CounterAccountId, t.RecurringPaymentId
            })
            .ToListAsync(cancellationToken);
        return rows
            .Select(t => new AiTransactionRecord(
                t.Id, TransactionDate.ToDateOnly(t.Date), t.PostedAt, t.Description, t.Category, t.LedgerCategory,
                t.Amount, t.AccountId, t.CounterAccountId, t.RecurringPaymentId))
            .ToList();
    }

    // Every row the filter selects, newest first, up to cap. Truncated says the cap was hit, so a
    // figure computed from these rows must be labelled approximate.
    public async Task<(IReadOnlyList<AiTransactionRecord> Rows, bool Truncated)> LoadAsync(
        IQueryable<Transaction> query,
        int cap,
        CancellationToken cancellationToken)
    {
        var rows = await ListAsync(query, "newest", cap + 1, cancellationToken);
        return rows.Count > cap ? (rows.Take(cap).ToList(), true) : (rows, false);
    }

    public sealed record AiTransactionAggregate(
        int Count,
        decimal ReportableOutflow,
        decimal ReportableInflow,
        DateOnly? FirstDate,
        DateOnly? LastDate);

    public async Task<AiTransactionAggregate> AggregateAsync(IQueryable<Transaction> query, CancellationToken cancellationToken)
    {
        var count = await query.CountAsync(cancellationToken);
        if (count == 0) return new AiTransactionAggregate(0, 0m, 0m, null, null);

        var reportable = Reportable(query);
        var outflow = await reportable.Where(t => t.Amount < 0).SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;
        var inflow = await reportable.Where(t => t.Amount > 0).SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;
        var first = await query.MinAsync(t => (DateTime?)t.Date, cancellationToken);
        var last = await query.MaxAsync(t => (DateTime?)t.Date, cancellationToken);
        return new AiTransactionAggregate(
            count,
            Math.Abs(outflow),
            inflow,
            first.HasValue ? TransactionDate.ToDateOnly(first.Value) : null,
            last.HasValue ? TransactionDate.ToDateOnly(last.Value) : null);
    }

    // Whether the ranges hold any visible row at all -- lets a tool tell "nothing matched" apart
    // from "there is no data in this period".
    public Task<bool> AnyInScopeAsync(IReadOnlyList<AiDateRange> ranges, CancellationToken cancellationToken) =>
        ApplyRanges(Visible(), ranges).AnyAsync(cancellationToken);

    public async Task<DateOnly?> EarliestDateAsync(CancellationToken cancellationToken)
    {
        var first = await Visible().MinAsync(t => (DateTime?)t.Date, cancellationToken);
        return first.HasValue ? TransactionDate.ToDateOnly(first.Value) : null;
    }

    private IQueryable<Transaction> Visible() =>
        _context.Transactions.AsNoTracking().Where(t => t.LedgerCategory != "Discarded");

    // Visible rows narrowed by every non-text filter; internal so the SQL translation tests can
    // compile the exact tree production sends to PostgreSQL.
    internal IQueryable<Transaction> Scoped(AiTransactionFilter filter) => ApplyFilters(Visible(), filter);

    internal static IOrderedQueryable<Transaction> Order(IQueryable<Transaction> query, string sort) => sort switch
    {
        "oldest" => query.OrderBy(t => t.Date).ThenBy(t => t.PostedAt).ThenBy(t => t.Id),
        "largest" => query.OrderByDescending(t => t.Amount < 0 ? -t.Amount : t.Amount).ThenByDescending(t => t.Date).ThenBy(t => t.Id),
        "smallest" => query.OrderBy(t => t.Amount < 0 ? -t.Amount : t.Amount).ThenByDescending(t => t.Date).ThenBy(t => t.Id),
        _ => query.OrderByDescending(t => t.Date).ThenByDescending(t => t.PostedAt).ThenByDescending(t => t.Id)
    };

    // SQL mirror of TransactionReportSemantics.IsReportableCashMovement.
    internal static IQueryable<Transaction> Reportable(IQueryable<Transaction> query) =>
        query.Where(t =>
            t.Amount != 0m &&
            t.Category.ToLower() != "transfer" &&
            t.Category.ToLower() != "adjustment" &&
            t.LedgerCategory.ToLower() != "discarded" &&
            t.LedgerCategory.ToLower() != "accountmove" &&
            !t.LedgerCategory.ToLower().StartsWith("transfer:"));

    private static IQueryable<Transaction> ApplyFilters(IQueryable<Transaction> query, AiTransactionFilter filter)
    {
        query = ApplyRanges(query, filter.Ranges);
        if (filter.ExcludeSystemRows) query = query.Where(t => !t.ExcludeFromAutocomplete);
        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            var category = filter.Category.Trim().ToLower();
            query = query.Where(t => t.Category.ToLower() == category);
        }
        if (!string.IsNullOrWhiteSpace(filter.LedgerCategory))
        {
            var ledger = filter.LedgerCategory.Trim().ToLower();
            query = ledger == "income"
                ? query.Where(t => t.LedgerCategory.ToLower() == "income" || t.LedgerCategory.ToLower().StartsWith("incomesplit:"))
                : query.Where(t => t.LedgerCategory.ToLower() == ledger);
        }
        query = filter.TxType switch
        {
            "outflow" => Reportable(query).Where(t => t.Amount < 0),
            "inflow" => Reportable(query).Where(t => t.Amount > 0),
            "income" => Reportable(query).Where(t => t.Amount > 0 &&
                (t.LedgerCategory.ToLower() == "income" || t.LedgerCategory.ToLower().StartsWith("incomesplit:"))),
            "transfer" => query.Where(t =>
                t.Category.ToLower() == "transfer" ||
                t.LedgerCategory.ToLower() == "accountmove" ||
                t.LedgerCategory.ToLower().StartsWith("transfer:")),
            _ => query
        };
        // Amount bounds compare magnitude, matching the Ledger filter bar.
        if (filter.MinAmount is { } min) query = query.Where(t => t.Amount >= min || t.Amount <= -min);
        if (filter.MaxAmount is { } max) query = query.Where(t => t.Amount <= max && t.Amount >= -max);
        if (!string.IsNullOrWhiteSpace(filter.AccountId))
        {
            var accountId = filter.AccountId;
            query = query.Where(t => t.AccountId == accountId || t.CounterAccountId == accountId);
        }
        return query;
    }

    private static IQueryable<Transaction> ApplyRanges(IQueryable<Transaction> query, IReadOnlyList<AiDateRange> ranges)
    {
        if (ranges.Count == 0) return query;
        // (Date >= s1 && Date < e1) || (Date >= s2 && Date < e2) ... built as one translatable predicate.
        var parameter = Expression.Parameter(typeof(Transaction), "t");
        var date = Expression.Property(parameter, nameof(Transaction.Date));
        Expression? body = null;
        foreach (var range in ranges)
        {
            var inRange = Expression.AndAlso(
                Expression.GreaterThanOrEqual(date, Expression.Constant(range.Start)),
                Expression.LessThan(date, Expression.Constant(range.End)));
            body = body == null ? inRange : Expression.OrElse(body, inRange);
        }
        return query.Where(Expression.Lambda<Func<Transaction, bool>>(body!, parameter));
    }

    private async Task<IQueryable<Transaction>> FuzzyAsync(
        IQueryable<Transaction> scoped,
        string query,
        CancellationToken cancellationToken)
    {
        if (UseNpgsql) return TransactionTextSearch.ApplyFuzzy(scoped, query);

        // Trigram functions exist only in PostgreSQL; elsewhere (tests) the same threshold is
        // applied in memory and narrowed back to an id filter so callers keep one query shape.
        var candidates = await scoped
            .Select(t => new { t.Id, t.Description, t.Category, t.LedgerCategory })
            .ToListAsync(cancellationToken);
        var ids = candidates
            .Where(t => TransactionTextSearch.IsFuzzyMatch(query, t.Description, t.Category, t.LedgerCategory))
            .Select(t => t.Id)
            .ToList();
        return scoped.Where(t => ids.Contains(t.Id));
    }
}
