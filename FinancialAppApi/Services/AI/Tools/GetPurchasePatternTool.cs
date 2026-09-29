using System.Text.Json.Nodes;

namespace FinancialAppApi.Services.AI.Tools;

// Cadence of a repeated purchase ("how often do I get a haircut", "when am I due for the next").
// The pattern arithmetic is AiAssistantService.BuildPurchaseFrequencyMetric, so the estimate
// matches what the assistant has always reported.
public sealed class GetPurchasePatternTool : IAiTool
{
    private const int MatchCap = 2000;

    private readonly AiTransactionQueryService _transactions;

    public GetPurchasePatternTool(AiTransactionQueryService transactions) => _transactions = transactions;

    public string Name => "get_purchase_pattern";

    public string Description =>
        "How often the user buys or does something (haircut, groceries, fuel, a car wash), across all saved history " +
        "unless 'since' is given: purchase count, first and last date, typical gap, purchases per cycle, days since " +
        "the last one, and the estimated next date. Use for 'how often do I...' and 'when am I due for...'.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("query", AiToolSchema.String("What was bought, e.g. \"haircut\".")),
        ("since", AiToolSchema.Date("Only consider purchases on or after this date."))
    ], "query");

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public string ProgressLabel(AiToolArgs args) =>
        args.OptionalString("query", 120) is { } query
            ? $"Checking how often you buy “{query}”"
            : "Checking your purchase pattern";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var query = args.RequiredString("query", 120);
        var since = args.OptionalDate("since");
        if (since > context.Today) throw new AiToolArgumentException("since cannot be in the future.");

        IReadOnlyList<AiDateRange> ranges = since.HasValue ? [AiDateRange.FromDates(since.Value, context.Today)] : [];
        var match = await _transactions.MatchAsync(
            new AiTransactionFilter(ranges, query, TxType: "outflow", ExcludeSystemRows: true),
            cancellationToken);
        var (rows, truncated) = await _transactions.LoadAsync(match.Query, MatchCap, cancellationToken);
        var scopeStart = since ?? await _transactions.EarliestDateAsync(cancellationToken);

        var metric = AiAssistantService.BuildPurchaseFrequencyMetric(
            query,
            match.MatchMode == "all" ? "exact" : match.MatchMode,
            rows.OrderBy(row => row.Date).ThenBy(row => row.Id, StringComparer.Ordinal)
                .Select(row => new AiAssistantService.AiPurchaseMatch(row.Id, row.Date, row.Description))
                .ToList(),
            scopeStart,
            context.Today,
            context.Today,
            context.CycleDay);

        context.Evidence.RecordAll(AiEvidenceLedger.Transaction, rows.Take(5).Select(row => row.Id));
        return AiToolResult.Of(new
        {
            metric,
            latest = rows.Take(3).Select(row => new
            {
                id = row.Id,
                date = row.Date.ToString("yyyy-MM-dd"),
                description = row.Description,
                amount = Math.Abs(row.Amount)
            }).ToList()
        }, truncated: truncated, approximate: truncated);
    }
}
