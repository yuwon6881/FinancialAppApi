using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Services.AI.Tools;

public sealed partial class GetSpendingBreakdownTool : IAiTool
{
    private const int RowCap = 20_000;
    private static readonly string[] GroupBys = ["category", "ledgerCategory", "merchant", "account", "weekday", "day"];
    private static readonly string[] TxTypes = ["outflow", "inflow", "income"];

    private readonly AiTransactionQueryService _transactions;
    private readonly LedgerAccountService _accounts;

    public GetSpendingBreakdownTool(AiTransactionQueryService transactions, LedgerAccountService accounts)
    {
        _transactions = transactions;
        _accounts = accounts;
    }

    public string Name => "get_spending_breakdown";

    public string Description =>
        "Break spending (or money in) down by category, ledger bucket, merchant, account, weekday, or day over a " +
        "period, largest first, with counts and each group's share of the total. Use for 'where does my money go', " +
        "'which merchants do I spend most at', or 'what day of the week do I spend most'. Defaults to the current cycle.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("groupBy", AiToolSchema.Enum("How to group. Default category.", GroupBys)),
        ("txType", AiToolSchema.Enum("outflow (default) = spending; inflow = any money in; income = Income-ledger money only.", TxTypes)),
        ("cycleKeys", AiToolSchema.StringArray("Cycles to cover: \"current\", \"previous\", or keys like \"2026-08\".", 12)),
        ("startDate", AiToolSchema.Date("First date to include, instead of cycles.")),
        ("endDate", AiToolSchema.Date("Last date to include, instead of cycles.")),
        ("top", AiToolSchema.Integer("Groups to return; the rest are totalled as 'other'. Default 10.", 1, 25))
    ]);

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public IReadOnlySet<string> AmountProperties => AiCycleSummaryCalculator.AmountProperties;

    public string ProgressLabel(AiToolArgs args) => "Breaking down your spending";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var groupBy = args.OptionalEnum("groupBy", GroupBys) ?? "category";
        var txType = args.OptionalEnum("txType", TxTypes) ?? "outflow";
        var top = args.OptionalInt("top", 1, 25) ?? 10;
        var (ranges, scope) = ResolveScope(args, context);

        var (rows, truncated) = await _transactions.LoadAsync(
            _transactions.Scoped(new AiTransactionFilter(ranges, TxType: txType)), RowCap, cancellationToken);
        var accountNames = groupBy == "account"
            ? (await _accounts.GetAccountsAsync(cancellationToken)).ToDictionary(account => account.Id, account => account.Name)
            : new Dictionary<string, string>();

        var total = Math.Abs(rows.Sum(row => row.Amount));
        var groups = rows
            .GroupBy(row => Key(row, groupBy), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                key = group.Key,
                label = Label(group, groupBy, accountNames),
                amount = Math.Abs(group.Sum(row => row.Amount)),
                count = group.Count()
            })
            .OrderByDescending(group => group.amount)
            .ThenBy(group => group.label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shown = groups.Take(top).ToList();
        var rest = groups.Skip(top).ToList();
        if (groupBy == "account") context.Evidence.RecordAll(AiEvidenceLedger.Account, shown.Select(group => group.key));

        return AiToolResult.Of(new
        {
            scope,
            groupBy,
            txType,
            total,
            transactionCount = rows.Count,
            groups = shown.Select(group => new
            {
                group.key,
                group.label,
                group.amount,
                group.count,
                share = total == 0 ? 0 : Math.Round(group.amount / total * 100m, 1)
            }).ToList(),
            other = rest.Count == 0 ? null : new { groupCount = rest.Count, amount = rest.Sum(group => group.amount), count = rest.Sum(group => group.count) }
        }, truncated: truncated, approximate: truncated);
    }

    private static (IReadOnlyList<AiDateRange> Ranges, object Scope) ResolveScope(AiToolArgs args, AiToolContext context)
    {
        var keys = args.OptionalStringArray("cycleKeys", 12, 16);
        var start = args.OptionalDate("startDate");
        var end = args.OptionalDate("endDate");
        if (keys.Count > 0 && (start.HasValue || end.HasValue))
            throw new AiToolArgumentException("Give either cycleKeys or startDate/endDate, not both.");
        if (start > end) throw new AiToolArgumentException("startDate must be on or before endDate.");

        if (start.HasValue || end.HasValue)
        {
            var first = start ?? new DateOnly(1900, 1, 1);
            var last = end ?? context.Today;
            return ([AiDateRange.FromDates(first, last)], new { from = start?.ToString("yyyy-MM-dd"), to = last.ToString("yyyy-MM-dd") });
        }
        var cycles = (keys.Count > 0 ? keys : ["current"])
            .Select(key => AiCycleResolver.Parse(key, context.Today, context.CycleDay))
            .Distinct()
            .ToList();
        return (AiCycleResolver.MergedRanges(cycles, context.CycleDay), new { cycles = cycles.Select(cycle => cycle.Key).ToList() });
    }

    private static string Key(AiTransactionRecord row, string groupBy) => groupBy switch
    {
        "ledgerCategory" => row.LedgerCategory,
        "merchant" => MerchantKey(row.Description),
        "account" => row.AccountId ?? "unassigned",
        "weekday" => row.Date.DayOfWeek.ToString(),
        "day" => row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => row.Category
    };

    private static string Label(IGrouping<string, AiTransactionRecord> group, string groupBy, IReadOnlyDictionary<string, string> accountNames) => groupBy switch
    {
        // The most frequent original spelling reads better than the normalized key.
        "merchant" => group.GroupBy(row => row.Description.Trim()).OrderByDescending(g => g.Count()).First().Key,
        "account" => accountNames.GetValueOrDefault(group.Key) ?? (group.Key == "unassigned" ? "No account" : group.Key),
        _ => group.Key
    };

    // "GRAB  Ride", "Grab ride" and "grab ride." are one merchant.
    private static string MerchantKey(string description) =>
        WhitespacePattern().Replace(PunctuationPattern().Replace(description.Trim().ToLowerInvariant(), " "), " ").Trim();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex PunctuationPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
