using System.Text.Json.Nodes;

namespace FinancialAppApi.Services.AI.Tools;

public sealed class CompareCyclesTool : IAiTool
{
    private const int RowCap = 20_000;

    private readonly AiTransactionQueryService _transactions;

    public CompareCyclesTool(AiTransactionQueryService transactions) => _transactions = transactions;

    public string Name => "compare_cycles";

    public string Description =>
        "Compare several financial cycles side by side: income, money in, spending, net change, top spending " +
        "categories and per-bucket net for each, plus the server-calculated change from the previous cycle in the " +
        "list. Use for trends, 'am I spending more than before', or 'compare March and April'. Give cycleKeys, or " +
        "lastN for the most recent N cycles ending with the current one.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("cycleKeys", AiToolSchema.StringArray("Cycles to compare: \"current\", \"previous\", or keys like \"2026-08\".", 12)),
        ("lastN", AiToolSchema.Integer("Compare the most recent N cycles ending with the current one. Default 3.", 2, 12))
    ]);

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public IReadOnlySet<string> AmountProperties => AiCycleSummaryCalculator.AmountProperties;

    public string ProgressLabel(AiToolArgs args) => "Comparing your cycles";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var keys = args.OptionalStringArray("cycleKeys", 12, 16);
        var lastN = args.OptionalInt("lastN", 2, 12);
        if (keys.Count > 0 && lastN.HasValue)
            throw new AiToolArgumentException("Give either cycleKeys or lastN, not both.");

        var current = AiCycleResolver.Current(context.Today, context.CycleDay);
        var cycles = (keys.Count > 0
                ? keys.Select(key => AiCycleResolver.Parse(key, context.Today, context.CycleDay))
                : Enumerable.Range(0, lastN ?? 3).Select(offset => current.AddCycles(-offset)))
            .Distinct()
            .OrderBy(cycle => cycle.Year).ThenBy(cycle => cycle.MonthIndex)
            .ToList();
        if (cycles.Count < 2) throw new AiToolArgumentException("Name at least two different cycles to compare.");

        var (rows, truncated) = await _transactions.LoadAsync(
            _transactions.Scoped(new AiTransactionFilter(AiCycleResolver.MergedRanges(cycles, context.CycleDay))),
            RowCap,
            cancellationToken);
        var summaries = cycles
            .Select(cycle => AiCycleSummaryCalculator.Build(cycle, context.CycleDay, context.Today, rows, includeInsights: false, topCategories: 5))
            .ToList();

        return AiToolResult.Of(new
        {
            cycles = summaries.Select((summary, index) => new
            {
                summary.Cycle,
                summary.Label,
                summary.Phase,
                summary.ObservedThrough,
                summary.HasTransactions,
                summary.TransactionCount,
                summary.Income,
                summary.Inflow,
                summary.Outflow,
                summary.NetChange,
                summary.CategorySpend,
                summary.LedgerNet,
                // Differences are computed here so the model never does the subtraction itself.
                outflowChange = index == 0 ? (decimal?)null : summary.Outflow - summaries[index - 1].Outflow,
                incomeChange = index == 0 ? (decimal?)null : summary.Income - summaries[index - 1].Income,
                netChangeChange = index == 0 ? (decimal?)null : summary.NetChange - summaries[index - 1].NetChange
            }).ToList(),
            note = summaries.Any(summary => summary.Phase == "InProgress")
                ? "A cycle marked InProgress is only partly over; compare it as 'so far', not as a finished cycle."
                : null
        }, truncated: truncated, approximate: truncated);
    }
}
