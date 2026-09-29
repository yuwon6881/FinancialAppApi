using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FinancialAppApi.Diagnostics;

namespace FinancialAppApi.Services.AI.Tools;

public sealed record AiToolExecution(string ToolName, string Label, string Output, bool Succeeded, bool Truncated, bool Approximate);

// Runs one model-requested call. Every failure becomes a model-readable error result rather than
// an exception, so a bad argument or an exhausted budget costs one round, never the whole turn.
// Calls run one at a time: the scoped DbContext every tool shares is not thread-safe.
public sealed class AiToolExecutor
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly AiToolRegistry _registry;
    private readonly ILogger<AiToolExecutor> _logger;
    // An identical repeated call in the same turn returns the first answer without another query.
    private readonly Dictionary<string, AiToolExecution> _turnCache = new(StringComparer.Ordinal);

    public AiToolExecutor(AiToolRegistry registry, ILogger<AiToolExecutor> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task<AiToolExecution> ExecuteAsync(
        AiFunctionCall call,
        AiToolContext context,
        CancellationToken cancellationToken)
    {
        var tool = _registry.Find(call.Name);
        if (tool == null)
        {
            return Failure(call.Name, "Checking your data",
                $"Unknown tool '{call.Name}'. Available tools: {string.Join(", ", _registry.Tools.Select(t => t.Name))}.");
        }

        AiToolArgs args;
        try
        {
            args = AiToolArgs.Parse(call.ArgumentsJson);
        }
        catch (AiToolArgumentException ex)
        {
            return Failure(tool.Name, tool.ProgressLabel(AiToolArgs.Empty), ex.Message);
        }

        var label = SafeLabel(tool, args);
        var cacheKey = $"{tool.Name}\n{args.CanonicalJson()}";
        if (_turnCache.TryGetValue(cacheKey, out var cached)) return cached;
        if (!context.Budget.TryReserveCall())
        {
            return Failure(tool.Name, label,
                "The tool-call budget for this answer is used up. Answer from the results you already have and say what is missing.");
        }

        await context.RefreshSensitiveModeAsync(cancellationToken);
        if (context.SensitiveMode && tool.Sensitivity == AiToolSensitivity.HiddenWhenSensitive)
        {
            return Remember(cacheKey, Failure(tool.Name, label,
                "Hidden by sensitive mode. Tell the user to unhide balances to see this."));
        }

        using var activity = Telemetry.ActivitySource.StartActivity("AiTool.Execute");
        activity?.SetTag("ai.tool", tool.Name);
        var startedAt = Stopwatch.GetTimestamp();
        AiToolResult result;
        try
        {
            result = await tool.ExecuteAsync(args, context, cancellationToken);
        }
        catch (AiToolArgumentException ex)
        {
            return Remember(cacheKey, Failure(tool.Name, label, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "AI tool {Tool} failed.", tool.Name);
            return Failure(tool.Name, label, "This lookup failed on the server. Do not guess the answer; tell the user it could not be checked.");
        }

        var data = JsonSerializer.SerializeToNode(result.Data, SerializerOptions);
        // Re-read after the tool ran: the setting may have flipped while the query was in flight.
        await context.RefreshSensitiveModeAsync(cancellationToken);
        if (context.SensitiveMode) AiSensitiveMasker.Mask(data, tool.AmountProperties);

        var envelope = new JsonObject
        {
            ["ok"] = true,
            ["sensitiveMode"] = context.SensitiveMode,
            ["data"] = data
        };
        if (result.Truncated) envelope["truncated"] = true;
        if (result.Approximate) envelope["approximate"] = true;
        var output = envelope.ToJsonString();

        _logger.LogInformation(
            "AI tool {Tool} ran in {ElapsedMs:F0} ms returning {Characters} characters.",
            tool.Name,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            output.Length);
        if (!context.Budget.TryConsumeCharacters(output.Length))
        {
            return Failure(tool.Name, label,
                "That result was too large to use. Narrow the request (a shorter date range, a smaller limit, or a specific search) and try again.");
        }

        return Remember(cacheKey, new AiToolExecution(tool.Name, label, output, true, result.Truncated, result.Approximate));
    }

    private AiToolExecution Remember(string key, AiToolExecution execution)
    {
        _turnCache[key] = execution;
        return execution;
    }

    private static string SafeLabel(IAiTool tool, AiToolArgs args)
    {
        try
        {
            return tool.ProgressLabel(args);
        }
        catch (AiToolArgumentException)
        {
            return tool.ProgressLabel(AiToolArgs.Empty);
        }
    }

    private static AiToolExecution Failure(string toolName, string label, string message) =>
        new(toolName, label, new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString(), false, false, false);
}
