using System.Text.Json.Nodes;

namespace FinancialAppApi.Services.AI.Tools;

// Finds ledger transactions. Its defaults widen rather than narrow: with no period it searches
// all saved history newest first, so "when was my latest haircut?" finds a haircut from months
// ago instead of reporting that the current cycle has none.
public sealed class SearchTransactionsTool : IAiTool
{
    private static readonly string[] TxTypes = ["outflow", "inflow", "income", "transfer"];
    private static readonly string[] Sorts = ["newest", "oldest", "largest", "smallest"];
    private static readonly string[] Matches = ["auto", "contains", "whole-word"];
    private static readonly DateOnly EarliestDate = new(1900, 1, 1);
    private static readonly DateOnly LatestDate = new(2100, 12, 31);

    private readonly AiTransactionQueryService _transactions;

    public SearchTransactionsTool(AiTransactionQueryService transactions) => _transactions = transactions;

    public string Name => "search_transactions";

    public string Description =>
        "Find the user's ledger transactions by text, date range, cycle, category, ledger bucket, type, amount, or account. " +
        "Searches ALL saved history unless a period is given. Returns matching rows plus exact totals over every match " +
        "(totalMatches, totalOutflow, totalInflow, firstDate, lastDate), so use it for 'when did I last...', 'how many " +
        "times...', 'how much did I spend on...', and for finding a record to edit or delete. Use sort=newest limit=1 " +
        "for the latest occurrence.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("query", AiToolSchema.String("Words to find in the description or category, e.g. \"haircut\" or \"grab\". Omit to list everything in scope.")),
        ("startDate", AiToolSchema.Date("First date to include.")),
        ("endDate", AiToolSchema.Date("Last date to include.")),
        ("cycleKeys", AiToolSchema.StringArray("Financial cycles to search instead of dates: \"current\", \"previous\", or keys like \"2026-08\".", 12)),
        ("category", AiToolSchema.String("Exact category name, e.g. \"Food\".")),
        ("ledgerCategory", AiToolSchema.Enum("Ledger bucket.", "Essentials", "Growth", "Stability", "Rewards", "Income")),
        ("txType", AiToolSchema.Enum("Money direction. outflow = spending; inflow = any money in; income = Income-ledger money only; transfer = moves between buckets or accounts.", TxTypes)),
        ("minAmount", AiToolSchema.Number("Smallest amount magnitude to include.", 0)),
        ("maxAmount", AiToolSchema.Number("Largest amount magnitude to include.", 0)),
        ("accountId", AiToolSchema.String("Only transactions on this ledger account id.")),
        ("sort", AiToolSchema.Enum("Row order. Default newest.", Sorts)),
        ("limit", AiToolSchema.Integer("Rows to return (totals always cover every match). Default 10.", 1, 50)),
        ("match", AiToolSchema.Enum("auto (default) also tries ignoring spaces and close spellings when nothing matches exactly; whole-word matches whole words only.", Matches))
    ]);

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public string ProgressLabel(AiToolArgs args) =>
        args.OptionalString("query", 200) is { } query
            ? $"Searching your transactions for “{query}”"
            : "Looking through your transactions";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var query = args.OptionalString("query", 200);
        var startDate = args.OptionalDate("startDate");
        var endDate = args.OptionalDate("endDate");
        var cycleKeys = args.OptionalStringArray("cycleKeys", 12, 16);
        var minAmount = args.OptionalDecimal("minAmount", 0m, 1_000_000_000m);
        var maxAmount = args.OptionalDecimal("maxAmount", 0m, 1_000_000_000m);
        var sort = args.OptionalEnum("sort", Sorts) ?? "newest";
        var limit = args.OptionalInt("limit", 1, 50) ?? 10;

        if (cycleKeys.Count > 0 && (startDate.HasValue || endDate.HasValue))
            throw new AiToolArgumentException("Give either cycleKeys or startDate/endDate, not both.");
        if (startDate > endDate)
            throw new AiToolArgumentException("startDate must be on or before endDate.");
        if (minAmount > maxAmount)
            throw new AiToolArgumentException("minAmount must not exceed maxAmount.");
        // Amount filters and size ordering reveal figures indirectly, so they wait for balances to be shown.
        if (context.SensitiveMode && (minAmount.HasValue || maxAmount.HasValue || sort is "largest" or "smallest"))
            throw new AiToolArgumentException("Sensitive mode is on: amount filters and largest/smallest ordering are unavailable. Search by text or date instead.");

        var cycles = cycleKeys.Select(key => AiCycleResolver.Parse(key, context.Today, context.CycleDay)).ToList();
        IReadOnlyList<AiDateRange> ranges = cycles.Count > 0
            ? AiCycleResolver.MergedRanges(cycles, context.CycleDay)
            : startDate.HasValue || endDate.HasValue
                ? [AiDateRange.FromDates(startDate ?? EarliestDate, endDate ?? LatestDate)]
                : [];

        var filter = new AiTransactionFilter(
            ranges,
            query,
            args.OptionalEnum("match", Matches) ?? "auto",
            args.OptionalString("category", 80),
            args.OptionalEnum("ledgerCategory", ["Essentials", "Growth", "Stability", "Rewards", "Income"]),
            args.OptionalEnum("txType", TxTypes),
            minAmount,
            maxAmount,
            args.OptionalString("accountId", 100));

        var match = await _transactions.MatchAsync(filter, cancellationToken);
        var aggregate = await _transactions.AggregateAsync(match.Query, cancellationToken);
        var rows = aggregate.Count == 0
            ? []
            : await _transactions.ListAsync(match.Query, sort, limit, cancellationToken);
        // Only asked when nothing matched: separates "no such purchase" from "no data in this period".
        bool? scopeHasTransactions = aggregate.Count == 0
            ? await _transactions.AnyInScopeAsync(ranges, cancellationToken)
            : null;

        context.Evidence.RecordAll(AiEvidenceLedger.Transaction, rows.Select(row => row.Id));
        context.Evidence.RecordAll(AiEvidenceLedger.Account, rows.SelectMany(row => new[] { row.AccountId, row.CounterAccountId }));

        return AiToolResult.Of(new
        {
            scope = new
            {
                allHistory = ranges.Count == 0,
                cycles = cycles.Count > 0 ? cycles.Select(cycle => cycle.Key).Distinct().ToList() : null,
                from = ranges.Count > 0 && cycles.Count == 0 && startDate.HasValue ? startDate.Value.ToString("yyyy-MM-dd") : null,
                to = ranges.Count > 0 && cycles.Count == 0 && endDate.HasValue ? endDate.Value.ToString("yyyy-MM-dd") : null
            },
            query,
            matchMode = match.MatchMode,
            totalMatches = aggregate.Count,
            returned = rows.Count,
            sort,
            totalOutflow = aggregate.Count > 0 ? aggregate.ReportableOutflow : (decimal?)null,
            totalInflow = aggregate.Count > 0 ? aggregate.ReportableInflow : (decimal?)null,
            firstDate = aggregate.FirstDate?.ToString("yyyy-MM-dd"),
            lastDate = aggregate.LastDate?.ToString("yyyy-MM-dd"),
            scopeHasTransactions,
            sampleDescriptions = match.MatchMode is "spacing" or "fuzzy"
                ? rows.Select(row => row.Description).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList()
                : null,
            rows = rows.Select(row => new
            {
                id = row.Id,
                date = row.Date.ToString("yyyy-MM-dd"),
                cycle = AiCycleResolver.CycleOf(row.Date, context.CycleDay).Key,
                description = row.Description,
                category = row.Category,
                ledgerCategory = row.LedgerCategory,
                amount = row.Amount,
                kind = Kind(row),
                accountId = row.AccountId,
                counterAccountId = row.CounterAccountId,
                recurring = row.RecurringPaymentId != null ? true : (bool?)null
            }).ToList()
        });
    }

    private static string Kind(AiTransactionRecord row)
    {
        if (TransactionReportSemantics.IsTransfer(row.Category, row.LedgerCategory)) return "transfer";
        if (TransactionReportSemantics.IsBalanceAdjustment(row.Category)) return "adjustment";
        if (row.Amount < 0) return "outflow";
        return TransactionReportSemantics.IsReportableIncome(row.Amount, row.Category, row.LedgerCategory) ? "income" : "inflow";
    }
}
