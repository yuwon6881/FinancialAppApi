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
        string? searchMode = null,
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
        string? reloadFilter = null,
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
                searchMode,
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
                wishlistFilter,
                reloadFilter);
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
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? forTransactionIds = null) =>
        _stabilityReloadStatusService.GetStatusMapAsync(cancellationToken, forTransactionIds);

    private async Task<IReadOnlyList<TransactionProjection>> ApplyStatusesAsync(
        IReadOnlyList<TransactionProjection> items,
        CancellationToken cancellationToken)
    {
        if (!items.Any(CanCarryStabilityReloadStatus)) return items;
        // Only these rows will render a status, so the replay only has to reach back far enough to
        // cover them -- otherwise a ten-row page still costs a full-history replay.
        var statusMap = await _stabilityReloadStatusService.GetStatusMapAsync(
            cancellationToken,
            items.Select(item => item.Id).ToList());
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
        string? searchMode = null,
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
        string? sort = null,
        string? reloadFilter = null,
        CancellationToken cancellationToken = default)
    {
        var query = ApplyAllFilters(
            _context.Transactions.AsNoTracking(),
            _context.Database.IsNpgsql(),
            search,
            searchMode,
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
            wishlistFilter,
            reloadFilter);
        var rows = ApplySort(query, sort)
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
                EscapeCsvTextField(t.Description),
                EscapeCsvTextField(t.Category),
                EscapeCsvTextField(DisplayLedgerAllocation(t.LedgerCategory)),
                EscapeCsvField(debit),
                EscapeCsvField(credit),
                EscapeCsvField(movement),
                EscapeCsvTextField(t.AccountName ?? string.Empty)
            }));
        }
        await writer.FlushAsync(cancellationToken);
    }

    public string GetTransactionsExportFileName() =>
        $"financial_ledger_{_financialClock.Today:yyyy-MM-dd}.csv";

}
