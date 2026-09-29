using System.Text.Json.Nodes;

namespace FinancialAppApi.Services.AI.Tools;

// Unusual spending and possible duplicates. Judging a spend as unusual needs history, so the
// detectors always see the requested cycles plus the five before them; only findings dated inside
// the requested cycles are reported.
public sealed class AnalyzeTransactionsTool : IAiTool
{
    private const int BaselineCycles = 5;
    private const int RowCap = 20_000;
    private static readonly string[] Kinds = ["anomalies", "duplicates", "review"];

    private readonly AiTransactionQueryService _transactions;

    public AnalyzeTransactionsTool(AiTransactionQueryService transactions) => _transactions = transactions;

    public string Name => "analyze_transactions";

    public string Description =>
        "Check spending for problems within cycles (default the current cycle). kind=anomalies finds unusually large " +
        "spends for their category; kind=duplicates finds likely double charges; kind=review returns at most three " +
        "ranked findings of either kind plus each cycle's spending, for a cycle review. Findings, amounts, and " +
        "confidence are server-calculated; explain them without inventing causes.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("kind", AiToolSchema.Enum("What to look for.", Kinds)),
        ("cycleKeys", AiToolSchema.StringArray("Cycles to check: \"current\" (default), \"previous\", or keys like \"2026-08\".", 6))
    ], "kind");

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public string ProgressLabel(AiToolArgs args) => args.OptionalEnum("kind", Kinds) switch
    {
        "duplicates" => "Looking for duplicate charges",
        "review" => "Reviewing your cycle",
        _ => "Looking for unusual spending"
    };

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var kind = args.OptionalEnum("kind", Kinds) ?? throw new AiToolArgumentException("kind is required.");
        var keys = args.OptionalStringArray("cycleKeys", 6, 16);
        var cycles = (keys.Count > 0 ? keys : ["current"])
            .Select(key => AiCycleResolver.Parse(key, context.Today, context.CycleDay))
            .Distinct()
            .OrderBy(cycle => cycle.Year).ThenBy(cycle => cycle.MonthIndex)
            .ToList();
        var baseline = cycles
            .Concat(Enumerable.Range(1, BaselineCycles).Select(offset => cycles[0].AddCycles(-offset)))
            .Distinct()
            .ToList();
        var (rows, truncated) = await _transactions.LoadAsync(
            _transactions.Scoped(new AiTransactionFilter(AiCycleResolver.MergedRanges(baseline, context.CycleDay))),
            RowCap,
            cancellationToken);

        var inScope = AiCycleResolver.MergedRanges(cycles, context.CycleDay);
        var datesById = rows.ToDictionary(row => row.Id, row => row.Date, StringComparer.Ordinal);
        bool InScope(string id) => datesById.TryGetValue(id, out var date) &&
            inScope.Any(range => date >= range.FirstDate && date <= range.LastDate);
        var legacyRows = AiLegacyRows.From(rows);

        var anomalies = AiAssistantService.DetectAnomalies(legacyRows)
            .Where(item => InScope(item.Id))
            .Select(item => new
            {
                kind = "unusual spending",
                ids = new[] { item.Id },
                date = datesById[item.Id].ToString("yyyy-MM-dd"),
                description = item.Description,
                category = (string?)item.Category,
                amount = Math.Abs(item.Amount),
                confidence = Math.Round(Math.Min(1d, item.Score / 10d), 2),
                evidence = item.Reason
            })
            .ToList();
        var duplicates = AiAssistantService.DetectDuplicates(legacyRows)
            .Where(item => item.Ids.Any(InScope))
            .Select(item => new
            {
                kind = "possible duplicate",
                ids = item.Ids.ToArray(),
                date = item.Ids.Where(datesById.ContainsKey).Select(id => datesById[id]).Max().ToString("yyyy-MM-dd"),
                description = item.Description,
                category = (string?)null,
                amount = Math.Abs(item.Amount),
                confidence = Math.Round(item.Confidence, 2),
                evidence = string.Join("; ", item.Reasons)
            })
            .ToList();

        var findings = kind switch
        {
            "anomalies" => anomalies,
            "duplicates" => duplicates,
            _ => anomalies.Concat(duplicates).OrderByDescending(item => item.confidence).Take(3).ToList()
        };
        context.Evidence.RecordAll(AiEvidenceLedger.Transaction, findings.SelectMany(item => item.ids));

        return AiToolResult.Of(new
        {
            kind,
            cycles = cycles.Select(cycle => cycle.Key).ToList(),
            findings,
            cycleSpending = kind == "review"
                ? cycles.Select(cycle => AiCycleSummaryCalculator.Build(cycle, context.CycleDay, context.Today, rows, includeInsights: false, topCategories: 3))
                    .Select(summary => new { summary.Cycle, summary.Phase, summary.TransactionCount, summary.Outflow, summary.CategorySpend })
                    .ToList()
                : null,
            note = findings.Count == 0
                ? "Nothing stood out. Categories with fewer than four past spends are never judged unusual."
                : null
        }, truncated: truncated, approximate: truncated);
    }
}
