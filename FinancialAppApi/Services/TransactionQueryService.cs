using System.Globalization;
using System.Text;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed record TransactionProjection(
    string Id,
    DateTime Date,
    DateTime PostedAt,
    string Description,
    string Category,
    string LedgerCategory,
    decimal Amount,
    string? RecurringPaymentId,
    int? WishlistItemId);

public sealed record TransactionListResult(
    IReadOnlyList<TransactionProjection> Items,
    int? Total = null,
    int? Page = null,
    int? PageSize = null);

public sealed record CsvExportResult(byte[] Bytes, string FileName);

public class TransactionQueryService
{
    private readonly AppDbContext _context;
    private readonly FinancialClock _financialClock;

    public TransactionQueryService(AppDbContext context, FinancialClock? financialClock = null)
    {
        _context = context;
        _financialClock = financialClock ?? FinancialClock.Utc;
    }

    public async Task<TransactionListResult> GetTransactionsAsync(
        string? queryMonth = null,
        int? queryYear = null,
        bool all = false,
        int page = 1,
        int pageSize = 10,
        string? search = null,
        string? ledgerCategory = null,
        string? category = null,
        string? txType = null,
        string? startDate = null,
        string? endDate = null,
        decimal? minAmount = null,
        decimal? maxAmount = null,
        bool recurringOnly = false,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        if (all)
        {
            var query = ApplyAllFilters(
                _context.Transactions.AsNoTracking(),
                search,
                ledgerCategory,
                category,
                txType,
                startDate,
                endDate,
                minAmount,
                maxAmount,
                recurringOnly);
            var total = await query.CountAsync(cancellationToken);

            var txs = await query
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.PostedAt)
                .ThenByDescending(t => t.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(t => new TransactionProjection(
                    t.Id,
                    t.Date,
                    t.PostedAt,
                    t.Description,
                    t.Category,
                    t.LedgerCategory,
                    t.Amount,
                    t.RecurringPaymentId,
                    t.WishlistItemId
                ))
                .ToListAsync(cancellationToken);

            return new TransactionListResult(txs, total, page, pageSize);
        }

        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null)
        {
            var txs = await _context.Transactions
                .AsNoTracking()
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.PostedAt)
                .ThenByDescending(t => t.Id)
                .Select(t => new TransactionProjection(
                    t.Id,
                    t.Date,
                    t.PostedAt,
                    t.Description,
                    t.Category,
                    t.LedgerCategory,
                    t.Amount,
                    t.RecurringPaymentId,
                    t.WishlistItemId
                ))
                .ToListAsync(cancellationToken);
            return new TransactionListResult(txs);
        }

        string activeMonth = queryMonth ?? setting.SelectedMonth;
        int activeYear = queryYear ?? setting.SelectedYear;

        var activeMonthIndex = Array.IndexOf(FinancialConstants.MonthAbbreviations, activeMonth) + 1;
        if (activeMonthIndex == 0) throw new ArgumentException("Invalid month.", nameof(queryMonth));

        var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(activeYear, activeMonthIndex, setting.CycleDay);
        var cycleStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(cycleStart));
        var cycleEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(cycleEnd));

        var filtered = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.LedgerCategory.ToUpper() != "DISCARDED" && t.Date >= cycleStartDate && t.Date < cycleEndExclusive)
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Select(t => new TransactionProjection(
                t.Id,
                t.Date,
                t.PostedAt,
                t.Description,
                t.Category,
                t.LedgerCategory,
                t.Amount,
                t.RecurringPaymentId,
                t.WishlistItemId
            ))
            .ToListAsync(cancellationToken);

        return new TransactionListResult(filtered);
    }

    public async Task<Transaction?> GetTransactionByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Transactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.LedgerCategory != "Discarded", cancellationToken);
    }

    public async Task<List<AutocompleteSuggestion>> GetAutocompleteSuggestionsAsync(CancellationToken cancellationToken = default)
    {
        var recentTxs = await _context.Transactions
            .Where(t => t.LedgerCategory != "Discarded"
                && !t.Id.Contains("-split-")
                && !t.LedgerCategory.StartsWith("Transfer:Income->"))
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Take(1000)
            .Select(t => new { t.Description, t.Category, t.LedgerCategory, t.Amount })
            .ToListAsync(cancellationToken);

        var suggestions = new List<AutocompleteSuggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tx in recentTxs)
        {
            var txType = tx.Amount >= 0 ? "inflow" : "outflow";
            var key = $"{txType}:{tx.Description.Trim()}";

            if (!seen.Contains(key))
            {
                seen.Add(key);
                suggestions.Add(new AutocompleteSuggestion
                {
                    Description = tx.Description.Trim(),
                    Category = tx.Category,
                    LedgerCategory = tx.LedgerCategory,
                    TxType = txType
                });
            }
        }

        return suggestions;
    }

    public async Task<CsvExportResult> ExportTransactionsAsync(
        string? search = null,
        string? ledgerCategory = null,
        string? category = null,
        string? txType = null,
        string? startDate = null,
        string? endDate = null,
        decimal? minAmount = null,
        decimal? maxAmount = null,
        bool recurringOnly = false,
        CancellationToken cancellationToken = default)
    {
        var query = ApplyAllFilters(
            _context.Transactions.AsNoTracking(),
            search,
            ledgerCategory,
            category,
            txType,
            startDate,
            endDate,
            minAmount,
            maxAmount,
            recurringOnly);
        var rows = await query
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Select(t => new { t.Date, t.Description, t.Category, t.LedgerCategory, t.Amount })
            .ToListAsync(cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine("Date,Description,Category,Ledger Allocation,Debit (Outflow),Credit (Inflow),Internal Movement");

        foreach (var t in rows)
        {
            var isTransfer = t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase);
            var isOutflow = t.Amount < 0;
            var debit = !isTransfer && isOutflow ? FormatAmount(Math.Abs(t.Amount)) : "";
            var credit = !isTransfer && !isOutflow ? FormatAmount(t.Amount) : "";
            var movement = isTransfer ? FormatAmount(Math.Abs(t.Amount)) : "";

            sb.AppendLine(string.Join(",", new[]
            {
                EscapeCsvField(TransactionDate.ToDateOnly(t.Date).ToString("yyyy-MM-dd")),
                EscapeCsvField(t.Description),
                EscapeCsvField(t.Category),
                EscapeCsvField(DisplayLedgerAllocation(t.LedgerCategory)),
                EscapeCsvField(debit),
                EscapeCsvField(credit),
                EscapeCsvField(movement)
            }));
        }

        var csvBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var preamble = Encoding.UTF8.GetPreamble();
        var output = new byte[preamble.Length + csvBytes.Length];
        Buffer.BlockCopy(preamble, 0, output, 0, preamble.Length);
        Buffer.BlockCopy(csvBytes, 0, output, preamble.Length, csvBytes.Length);

        return new CsvExportResult(output, $"financial_ledger_{_financialClock.Today:yyyy-MM-dd}.csv");
    }

    private static IQueryable<Transaction> ApplyAllFilters(
        IQueryable<Transaction> query,
        string? search,
        string? ledgerCategory,
        string? category,
        string? txType,
        string? startDate,
        string? endDate,
        decimal? minAmount,
        decimal? maxAmount,
        bool recurringOnly)
    {
        query = query.Where(t => t.LedgerCategory != "Discarded");

        if (TransactionDate.TryParseInputDate(startDate, out var startDateOnly))
        {
            query = query.Where(t => t.Date >= TransactionDate.StartOfDate(startDateOnly));
        }
        if (TransactionDate.TryParseInputDate(endDate, out var endDateOnly))
        {
            query = query.Where(t => t.Date < TransactionDate.ExclusiveEndOfDate(endDateOnly));
        }

        if (minAmount is >= 0)
        {
            query = query.Where(t => Math.Abs(t.Amount) >= minAmount.Value);
        }
        if (maxAmount is >= 0)
        {
            query = query.Where(t => Math.Abs(t.Amount) <= maxAmount.Value);
        }

        if (recurringOnly)
        {
            query = query.Where(t => t.RecurringPaymentId != null && t.RecurringPaymentId != "");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(t =>
                t.Description.ToLower().Contains(s) ||
                t.Category.ToLower().Contains(s) ||
                t.LedgerCategory.ToLower().Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(ledgerCategory))
        {
            var buckets = ledgerCategory.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.Trim().ToLower()).ToList();

            query = query.Where(t =>
                buckets.Any(bucket =>
                    bucket == "income"
                        ? (t.LedgerCategory.ToLower() == "income" || t.LedgerCategory.ToLower().StartsWith("incomesplit:"))
                        : (t.LedgerCategory.ToLower() == bucket || t.LedgerCategory.ToLower().Contains(bucket))));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var cats = category.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim().ToLower()).ToList();
            query = query.Where(t => cats.Contains(t.Category.ToLower()));
        }

        if (!string.IsNullOrWhiteSpace(txType))
        {
            if (txType == "inflow")
                query = query.Where(t => t.Amount > 0 && t.Category != "Transfer" && !t.LedgerCategory.StartsWith("Transfer:"));
            else if (txType == "outflow")
                query = query.Where(t => t.Amount < 0 && t.Category != "Transfer" && !t.LedgerCategory.StartsWith("Transfer:"));
            else if (txType == "transfer")
                query = query.Where(t => t.Category == "Transfer" || t.LedgerCategory.StartsWith("Transfer:"));
        }

        return query;
    }

    private static string EscapeCsvField(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static string FormatAmount(decimal amount)
    {
        return amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string DisplayLedgerAllocation(string ledgerCategory)
    {
        if (ledgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase)) return "Income";
        if (ledgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
            return ledgerCategory.Substring("Transfer:".Length).Replace("->", " -> ");
        return ledgerCategory;
    }
}
