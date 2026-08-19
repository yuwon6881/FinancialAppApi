using System.Globalization;
using System.Text;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Services;

public sealed record TransactionProjection(
    string Id,
    DateTime Date,
    DateTime PostedAt,
    string Description,
    string Category,
    string LedgerCategory,
    decimal Amount,
    decimal? StabilityRecoveryTopUpAmount,
    string StabilityReloadIntent,
    string? RecurringPaymentId,
    DateOnly? RecurringOccurrenceDate,
    int? WishlistItemId,
    int? SavingsGoalId,
    string? StabilityReloadStatus = null,
    string? AccountId = null,
    string? CounterAccountId = null,
    bool ExcludeFromAutocomplete = false,
    bool IsAccountBalanceAdjustment = false);

public sealed record TransactionListResult(
    IReadOnlyList<TransactionProjection> Items,
    int? Total = null,
    int? Page = null,
    int? PageSize = null);

public partial class TransactionQueryService
{
    private readonly AppDbContext _context;
    private readonly FinancialClock _financialClock;
    private readonly StabilityReloadStatusService _stabilityReloadStatusService;

    public TransactionQueryService(
        AppDbContext context,
        FinancialClock? financialClock = null,
        StabilityReloadStatusService? stabilityReloadStatusService = null)
    {
        _context = context;
        _financialClock = financialClock ?? FinancialClock.Utc;
        _stabilityReloadStatusService = stabilityReloadStatusService
            ?? new StabilityReloadStatusService(context);
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
        bool wishlistOnly = false,
        string? sort = null,
        string? recurringFilter = null,
        string? wishlistFilter = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        if (all)
        {
            var query = ApplyAllFilters(
                _context.Transactions.AsNoTracking(),
                _context.Database.IsNpgsql(),
                search,
                ledgerCategory,
                category,
                txType,
                startDate,
                endDate,
                minAmount,
                maxAmount,
                recurringOnly,
                wishlistOnly,
                recurringFilter,
                wishlistFilter);
            var total = await query.CountAsync(cancellationToken);

            var txs = await ApplySort(query, sort)
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
                    t.StabilityRecoveryTopUpAmount,
                    t.StabilityReloadIntent,
                    t.RecurringPaymentId,
                    t.RecurringOccurrenceDate,
                    t.WishlistItemId,
                    t.SavingsGoalId,
                null,
                t.AccountId,
                t.CounterAccountId,
                t.ExcludeFromAutocomplete,
                t.IsAccountBalanceAdjustment
                ))
                .ToListAsync(cancellationToken);

            return new TransactionListResult(
                await ApplyStatusesAsync(txs, cancellationToken),
                total,
                page,
                pageSize);
        }

        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null)
        {
            // No settings row means no cycle to bound the query by; cap the result so
            // this branch can never materialize an unbounded table scan.
            var txs = await _context.Transactions
                .AsNoTracking()
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.PostedAt)
                .ThenByDescending(t => t.Id)
                .Take(500)
                .Select(t => new TransactionProjection(
                    t.Id,
                    t.Date,
                    t.PostedAt,
                    t.Description,
                    t.Category,
                    t.LedgerCategory,
                    t.Amount,
                    t.StabilityRecoveryTopUpAmount,
                    t.StabilityReloadIntent,
                    t.RecurringPaymentId,
                    t.RecurringOccurrenceDate,
                    t.WishlistItemId,
                    t.SavingsGoalId,
                    null,
                    t.AccountId,
                    t.CounterAccountId,
                    t.ExcludeFromAutocomplete,
                    t.IsAccountBalanceAdjustment
                ))
                .ToListAsync(cancellationToken);
            return new TransactionListResult(
                await ApplyStatusesAsync(txs, cancellationToken));
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
            .Where(t => t.LedgerCategory != "Discarded" && t.Date >= cycleStartDate && t.Date < cycleEndExclusive)
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
                t.StabilityRecoveryTopUpAmount,
                t.StabilityReloadIntent,
                t.RecurringPaymentId,
                t.RecurringOccurrenceDate,
                t.WishlistItemId,
                t.SavingsGoalId,
                null,
                t.AccountId,
                t.CounterAccountId,
                    t.ExcludeFromAutocomplete,
                    t.IsAccountBalanceAdjustment
            ))
            .ToListAsync(cancellationToken);

        return new TransactionListResult(
            await ApplyStatusesAsync(filtered, cancellationToken));
    }

    public Task<IReadOnlyDictionary<string, string>> GetStabilityReloadStatusMapAsync(
        CancellationToken cancellationToken = default) =>
        _stabilityReloadStatusService.GetStatusMapAsync(cancellationToken);

    private async Task<IReadOnlyList<TransactionProjection>> ApplyStatusesAsync(
        IReadOnlyList<TransactionProjection> items,
        CancellationToken cancellationToken)
    {
        if (!items.Any(CanCarryStabilityReloadStatus)) return items;
        var statusMap = await _stabilityReloadStatusService.GetStatusMapAsync(cancellationToken);
        return items
            .Select(item => statusMap.TryGetValue(item.Id, out var status)
                ? item with { StabilityReloadStatus = status }
                : item)
            .ToList();
    }

    internal static bool CanCarryStabilityReloadStatus(TransactionProjection item) =>
        StabilityReloadLedger.CanCarryReloadStatus(new Transaction
        {
            Id = item.Id,
            Category = item.Category,
            LedgerCategory = item.LedgerCategory,
            Amount = item.Amount,
            StabilityRecoveryTopUpAmount = item.StabilityRecoveryTopUpAmount,
            StabilityReloadIntent = item.StabilityReloadIntent,
            IsAccountBalanceAdjustment = item.IsAccountBalanceAdjustment,
        });

    public async Task<Transaction?> GetTransactionByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Transactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.LedgerCategory != "Discarded", cancellationToken);
    }

    public async Task<List<AutocompleteSuggestion>> GetAutocompleteSuggestionsAsync(CancellationToken cancellationToken = default)
    {
        var recentTxs = await _context.Transactions
            .Where(t => !t.ExcludeFromAutocomplete)
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Take(300)
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

    public async Task WriteTransactionsCsvAsync(
        Stream destination,
        string? search = null,
        string? ledgerCategory = null,
        string? category = null,
        string? txType = null,
        string? startDate = null,
        string? endDate = null,
        decimal? minAmount = null,
        decimal? maxAmount = null,
        bool recurringOnly = false,
        bool wishlistOnly = false,
        string? recurringFilter = null,
        string? wishlistFilter = null,
        CancellationToken cancellationToken = default)
    {
        var query = ApplyAllFilters(
            _context.Transactions.AsNoTracking(),
            _context.Database.IsNpgsql(),
            search,
            ledgerCategory,
            category,
            txType,
            startDate,
            endDate,
            minAmount,
            maxAmount,
            recurringOnly,
            wishlistOnly,
            recurringFilter,
            wishlistFilter);
        var rows = query
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Select(t => new
            {
                t.Date,
                t.Description,
                t.Category,
                t.LedgerCategory,
                t.Amount,
                AccountName = _context.LedgerAccounts
                    .Where(account => account.Id == t.AccountId)
                    .Select(account => account.Name)
                    .FirstOrDefault()
            });

        await using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            bufferSize: 16 * 1024,
            leaveOpen: true);
        await writer.WriteLineAsync(
            "Date,Description,Category,Ledger Allocation,Debit (Outflow),Credit (Inflow),Internal Movement,Account");

        await foreach (var t in rows
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken))
        {
            var isTransfer = t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.LedgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase);
            var isOutflow = t.Amount < 0;
            var debit = !isTransfer && isOutflow ? FormatAmount(Math.Abs(t.Amount)) : "";
            var credit = !isTransfer && !isOutflow ? FormatAmount(t.Amount) : "";
            var movement = isTransfer ? FormatAmount(Math.Abs(t.Amount)) : "";

            await writer.WriteLineAsync(string.Join(",", new[]
            {
                EscapeCsvField(TransactionDate.ToDateOnly(t.Date).ToString("yyyy-MM-dd")),
                EscapeCsvField(t.Description),
                EscapeCsvField(t.Category),
                EscapeCsvField(DisplayLedgerAllocation(t.LedgerCategory)),
                EscapeCsvField(debit),
                EscapeCsvField(credit),
                EscapeCsvField(movement),
                EscapeCsvField(t.AccountName ?? string.Empty)
            }));
        }
        await writer.FlushAsync(cancellationToken);
    }

    public string GetTransactionsExportFileName() =>
        $"financial_ledger_{_financialClock.Today:yyyy-MM-dd}.csv";

    private static IQueryable<Transaction> ApplyAllFilters(
        IQueryable<Transaction> query,
        bool useIlike,
        string? search,
        string? ledgerCategory,
        string? category,
        string? txType,
        string? startDate,
        string? endDate,
        decimal? minAmount,
        decimal? maxAmount,
        bool recurringOnly,
        bool wishlistOnly,
        string? recurringFilter,
        string? wishlistFilter)
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

        // Compare the raw signed amount against ±bound instead of Math.Abs(Amount): the
        // abs() form is non-sargable (the planner cannot use an index on Amount), whereas
        // the OR/AND range form is. Semantics are identical for the inflow(+)/outflow(-)
        // sign convention.
        if (minAmount is >= 0)
        {
            var min = minAmount.Value;
            query = query.Where(t => t.Amount >= min || t.Amount <= -min);
        }
        if (maxAmount is >= 0)
        {
            var max = maxAmount.Value;
            query = query.Where(t => t.Amount <= max && t.Amount >= -max);
        }

        var recurringMode = NormalizeLinkFilter(recurringFilter, recurringOnly);
        if (recurringMode == "only")
        {
            query = query.Where(t => t.RecurringPaymentId != null && t.RecurringPaymentId != "");
        }
        else if (recurringMode == "exclude")
        {
            query = query.Where(t => t.RecurringPaymentId == null || t.RecurringPaymentId == "");
        }

        var wishlistMode = NormalizeLinkFilter(wishlistFilter, wishlistOnly);
        if (wishlistMode == "only")
        {
            query = query.Where(t => t.WishlistItemId != null);
        }
        else if (wishlistMode == "exclude")
        {
            query = query.Where(t => t.WishlistItemId == null);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (useIlike)
            {
                // ILIKE is case-insensitive at the DB level and, unlike ToLower().Contains(),
                // can be served by the pg_trgm GIN indexes (see the AddTransactionSearchTrgmIndexes
                // migration). Escape LIKE wildcards so a literal % or _ typed by the user is not
                // treated as a pattern; '\' is the default ILIKE escape character.
                var pattern = "%" + EscapeLikePattern(term) + "%";
                query = query.Where(t =>
                    EF.Functions.ILike(t.Description, pattern) ||
                    EF.Functions.ILike(t.Category, pattern) ||
                    EF.Functions.ILike(t.LedgerCategory, pattern));
            }
            else
            {
                // Providers without ILIKE (e.g. the InMemory test provider) fall back to the
                // original case-insensitive substring match.
                var s = term.ToLower();
                query = query.Where(t =>
                    t.Description.ToLower().Contains(s) ||
                    t.Category.ToLower().Contains(s) ||
                    t.LedgerCategory.ToLower().Contains(s));
            }
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
                query = query.Where(t =>
                    t.Amount > 0 &&
                    t.Category.ToLower() != "transfer" &&
                    t.Category.ToLower() != "adjustment" &&
                    !t.LedgerCategory.ToLower().StartsWith("transfer:") &&
                    t.LedgerCategory.ToLower() != "discarded");
            else if (txType == "outflow")
                query = query.Where(t =>
                    t.Amount < 0 &&
                    t.Category.ToLower() != "transfer" &&
                    t.Category.ToLower() != "adjustment" &&
                    !t.LedgerCategory.ToLower().StartsWith("transfer:") &&
                    t.LedgerCategory.ToLower() != "discarded");
            else if (txType == "transfer")
                query = query.Where(t =>
                    t.Category.ToLower() == "transfer" ||
                    t.LedgerCategory.ToLower().StartsWith("transfer:"));
        }

        return query;
    }

    private static string NormalizeLinkFilter(string? filter, bool legacyOnly) =>
        filter?.Trim().ToLowerInvariant() switch
        {
            "only" => "only",
            "exclude" => "exclude",
            _ => legacyOnly ? "only" : "all"
        };

    private static IOrderedQueryable<Transaction> ApplySort(
        IQueryable<Transaction> query,
        string? sort)
    {
        return sort switch
        {
            "date-asc" => query
                .OrderBy(t => t.Date)
                .ThenBy(t => t.PostedAt)
                .ThenBy(t => t.Id),
            "amount-desc" => query
                .OrderByDescending(t => t.Amount < 0 ? -t.Amount : t.Amount)
                .ThenByDescending(t => t.Date)
                .ThenByDescending(t => t.PostedAt)
                .ThenByDescending(t => t.Id),
            "amount-asc" => query
                .OrderBy(t => t.Amount < 0 ? -t.Amount : t.Amount)
                .ThenByDescending(t => t.Date)
                .ThenByDescending(t => t.PostedAt)
                .ThenByDescending(t => t.Id),
            _ => query
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.PostedAt)
                .ThenByDescending(t => t.Id)
        };
    }

    // Escapes the ILIKE special characters so user-typed text is matched literally.
    // Backslash first (it is the escape character), then the wildcards % and _.
    private static string EscapeLikePattern(string input) => input
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

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
        if (string.Equals(ledgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase)) return "Between accounts";
        if (ledgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase)) return "Income";
        if (ledgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
            return ledgerCategory.Substring("Transfer:".Length).Replace("->", " -> ");
        return ledgerCategory;
    }
}
