using System.Text.Json;
using System.Text.Json.Nodes;
using FinancialAppApi.Services.AI.Tools;

namespace FinancialAppApi.Services.AI.Agent;

// The small picture of the user's finances sent with every turn, so an open question ("am I okay
// this month?") never starts from nothing and the model can pick the right tool for detail.
// Each part comes from the same tool the model would call, run directly without spending the
// model's tool budget, then trimmed. A part that fails is left out rather than failing the turn.
public sealed class AiBaselineSnapshotBuilder
{
    private const int CalendarCycles = 12;
    private const int TopCategories = 3;

    private readonly AiToolRegistry _tools;
    private readonly AiTransactionQueryService _transactions;
    private readonly ILogger<AiBaselineSnapshotBuilder> _logger;

    public AiBaselineSnapshotBuilder(
        AiToolRegistry tools,
        AiTransactionQueryService transactions,
        ILogger<AiBaselineSnapshotBuilder> logger)
    {
        _tools = tools;
        _transactions = transactions;
        _logger = logger;
    }

    public async Task<JsonObject> BuildAsync(AiToolContext context, CancellationToken cancellationToken)
    {
        var current = AiCycleResolver.Current(context.Today, context.CycleDay);
        var snapshot = new JsonObject
        {
            ["today"] = context.Today.ToString("yyyy-MM-dd"),
            ["currency"] = context.Currency,
            ["sensitiveMode"] = context.SensitiveMode,
            ["cycleStartDay"] = context.CycleDay,
            // Key -> date range for recent cycles, so no cycle is ever re-derived from a calendar month.
            ["cycles"] = new JsonArray(Enumerable.Range(0, CalendarCycles)
                .Select(offset => current.AddCycles(-offset))
                .Select(cycle =>
                {
                    var range = AiCycleResolver.Range(cycle, context.CycleDay);
                    var entry = new JsonObject
                    {
                        ["key"] = cycle.Key,
                        ["from"] = range.FirstDate.ToString("yyyy-MM-dd"),
                        ["to"] = range.LastDate.ToString("yyyy-MM-dd")
                    };
                    if (cycle == current) entry["label"] = "current";
                    else if (cycle == current.AddCycles(-1)) entry["label"] = "previous";
                    return (JsonNode)entry;
                }).ToArray())
        };

        snapshot["firstTransactionDate"] = await PartAsync("coverage", async () =>
            (await _transactions.EarliestDateAsync(cancellationToken)) is { } first
                ? JsonValue.Create(first.ToString("yyyy-MM-dd"))
                : JsonValue.Create("none saved yet"));
        snapshot["accounts"] = await ToolPartAsync("get_accounts", "{}", context, cancellationToken);

        var cycle = await ToolPartAsync("get_cycle_summary", "{}", context, cancellationToken);
        if (cycle is JsonObject cycleObject)
        {
            var trimmed = new JsonObject();
            foreach (var key in new[] { "cycle", "phase", "observedThrough", "transactionCount", "income", "inflow", "outflow", "netChange" })
                if (cycleObject[key] is { } value) trimmed[key] = value.DeepClone();
            if (cycleObject["categorySpend"] is JsonArray spend)
                trimmed["topSpending"] = new JsonArray(spend.Take(TopCategories).Select(item => item?.DeepClone()).ToArray());
            snapshot["currentCycle"] = trimmed;
        }

        var recurring = await ToolPartAsync("get_recurring", """{"status":"active"}""", context, cancellationToken);
        if (recurring?["upcoming"] is JsonArray upcoming)
            snapshot["upcomingBills"] = new JsonArray(upcoming.Take(3).Select(item => item?.DeepClone()).ToArray());

        var limits = await ToolPartAsync("get_category_limits", "{}", context, cancellationToken);
        var attention = (limits?["limits"] as JsonArray)?
            .Where(item => item?["status"]?.GetValue<string>() is "Watch" or "Exceeded")
            .Select(item => item?.DeepClone())
            .ToArray();
        if (attention is { Length: > 0 }) snapshot["categoryLimitsNeedingAttention"] = new JsonArray(attention);

        return snapshot;
    }

    private async Task<JsonNode?> ToolPartAsync(string name, string arguments, AiToolContext context, CancellationToken cancellationToken) =>
        await PartAsync(name, async () =>
        {
            var tool = _tools.Find(name);
            if (tool == null) return null;
            if (context.SensitiveMode && tool.Sensitivity == AiToolSensitivity.HiddenWhenSensitive) return null;
            var result = await tool.ExecuteAsync(AiToolArgs.Parse(arguments), context, cancellationToken);
            var node = JsonSerializer.SerializeToNode(result.Data, AiToolExecutor.SerializerOptions);
            if (context.SensitiveMode) AiSensitiveMasker.Mask(node, tool.AmountProperties);
            return node;
        });

    private async Task<JsonNode?> PartAsync(string name, Func<Task<JsonNode?>> build)
    {
        try
        {
            return await build();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Ask AI baseline part {Part} failed; the turn continues without it.", name);
            return null;
        }
    }
}
