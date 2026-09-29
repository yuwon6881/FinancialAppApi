using System.Text.Json.Nodes;

namespace FinancialAppApi.Services.AI.Tools;

public sealed class GetCycleSummaryTool : IAiTool
{
    // A cycle holds a few hundred rows at most; this ceiling only guards a pathological import.
    internal const int RowCap = 5000;

    private readonly AiTransactionQueryService _transactions;

    public GetCycleSummaryTool(AiTransactionQueryService transactions) => _transactions = transactions;

    public string Name => "get_cycle_summary";

    public string Description =>
        "Summarise one financial cycle: income, all money in, spending, net change, spending by category, " +
        "net change per ledger bucket, and progress insights (days elapsed/remaining, average daily spend, " +
        "no-spend days, biggest spending day, largest expense, committed vs discretionary spend). " +
        "Use for 'how am I doing this cycle', 'how much did I spend last cycle', or a cycle recap.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("cycleKey", AiToolSchema.String("\"current\" (default), \"previous\", or a cycle key like \"2026-08\"."))
    ]);

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public IReadOnlySet<string> AmountProperties => AiCycleSummaryCalculator.AmountProperties;

    public string ProgressLabel(AiToolArgs args) => "Summarising your cycle";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var cycle = AiCycleResolver.Parse(args.OptionalString("cycleKey", 16) ?? "current", context.Today, context.CycleDay);
        var range = AiCycleResolver.Range(cycle, context.CycleDay);
        var (rows, truncated) = await _transactions.LoadAsync(
            _transactions.Scoped(new AiTransactionFilter([range])), RowCap, cancellationToken);

        var summary = AiCycleSummaryCalculator.Build(cycle, context.CycleDay, context.Today, rows, includeInsights: true);
        if (summary.Insights?.LargestExpense is { } largest) context.Evidence.Record(AiEvidenceLedger.Transaction, largest.Id);
        return AiToolResult.Of(summary, truncated: truncated, approximate: truncated);
    }
}
