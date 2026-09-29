using System.Text.Json.Nodes;
using FinancialAppApi.Services.AI.Tools;

namespace FinancialAppApi.Services.AI.Agent;

// Runs one assistant turn: the model reads, calls read-only tools, proposes UI actions for
// server validation, and answers. The loop is bounded by rounds and by the tool budget; at the
// limit the model must answer from what it already has.
public sealed class AiAgentEngine
{
    public const int MaxToolRounds = 4;
    private const int MaxOutputTokens = 2400;
    private const string PromptCacheKey = "ask-ai-v3";

    private readonly AiClient _client;
    private readonly AiToolRegistry _registry;
    private readonly AiToolExecutor _executor;
    private readonly ILogger<AiAgentEngine> _logger;

    public AiAgentEngine(AiClient client, AiToolRegistry registry, AiToolExecutor executor, ILogger<AiAgentEngine> logger)
    {
        _client = client;
        _registry = registry;
        _executor = executor;
        _logger = logger;
    }

    public async Task<AiAgentTurnResult> RunAsync(AiAgentTurnRequest request, CancellationToken cancellationToken)
    {
        // The read tools come first in their stable order and the per-user action schema last,
        // so everything before it stays inside the cached prompt prefix.
        var tools = _registry.Definitions
            .Append(new AiFunctionTool(
                AiAgentPrompt.ProposeActionsTool,
                "Propose app actions for the user to review: open a screen, prepare add/edit drafts, or ask the app to " +
                "confirm a change. Returns accepted or rejected with a reason for each action.",
                request.ActionProposer.ParametersSchema))
            .ToList();
        var input = new List<JsonObject>(request.PriorInput) { AiInputItems.Message("user", request.UserMessage) };
        var trace = new List<AiToolTrace>();
        var usage = AiTokenUsage.None;
        var anyApproximate = false;
        var analysisCalls = 0;
        var streamSink = request.Sink == null ? null : new StreamRelay(request.Sink);

        if (request.SeededCalls is { Count: > 0 } seeded)
        {
            foreach (var (call, index) in seeded.Select((call, index) => (call, index)))
            {
                var functionCall = new AiFunctionCall($"seed_{index}", call.ToolName, call.ArgumentsJson);
                var execution = await ExecuteAsync(functionCall, request, cancellationToken);
                anyApproximate |= execution.Approximate || execution.Truncated;
                trace.Add(new AiToolTrace(call.ToolName, call.ArgumentsJson, execution.Succeeded));
                input.Add(AiInputItems.FunctionCall(functionCall.CallId, call.ToolName, call.ArgumentsJson));
                input.Add(AiInputItems.FunctionCallOutput(functionCall.CallId, execution.Output));
            }
        }

        for (var round = 1; ; round++)
        {
            var atLimit = round > MaxToolRounds || request.ToolContext.Budget.RemainingToolCalls <= 0;
            if (atLimit) input.Add(AiInputItems.Message("developer", AiAgentPrompt.ToolLimitNote));
            var toolChoice = atLimit ? "none" : round == 1 && request.ForcedFirstTool != null ? request.ForcedFirstTool : "auto";

            var result = await _client.CreateResponseAsync(
                new AiResponseRequest(
                    "chat-agent",
                    input,
                    MaxOutputTokens,
                    "OpenAiModels:Chat",
                    Instructions: AiAgentPrompt.Instructions,
                    Tools: tools,
                    ToolChoice: toolChoice,
                    // Several analysis lookups mean a harder question; think a little longer.
                    ReasoningEffort: analysisCalls >= 2 ? "medium" : "low",
                    PromptCacheKey: PromptCacheKey),
                streamSink,
                cancellationToken);
            usage = Add(usage, result.Usage);
            input.AddRange(result.OutputItems);

            if (result.FunctionCalls.Count == 0 || atLimit)
            {
                var reply = result.Text.Length > 0
                    ? result.Text
                    : "I couldn't finish looking that up. Please try asking again.";
                _logger.LogInformation(
                    "Ask AI turn finished in {Rounds} round(s) with {ToolCalls} tool call(s).", round, trace.Count);
                return new AiAgentTurnResult(
                    reply, request.ActionProposer.Accepted, trace, usage, round, anyApproximate, atLimit);
            }

            if (request.Sink != null && result.Text.Length > 0) await request.Sink.OnTextResetAsync(cancellationToken);
            foreach (var call in result.FunctionCalls)
            {
                string output;
                bool succeeded;
                if (call.Name == AiAgentPrompt.ProposeActionsTool)
                {
                    if (request.Sink != null) await request.Sink.OnStatusAsync("Preparing that for you", cancellationToken);
                    output = await request.ActionProposer.ProposeAsync(call.ArgumentsJson, cancellationToken);
                    succeeded = true;
                }
                else
                {
                    var execution = await ExecuteAsync(call, request, cancellationToken);
                    output = execution.Output;
                    succeeded = execution.Succeeded;
                    anyApproximate |= execution.Approximate || execution.Truncated;
                    if (call.Name is "compare_cycles" or "get_spending_breakdown" or "analyze_transactions" or "forecast_ledger_balance")
                        analysisCalls++;
                }
                trace.Add(new AiToolTrace(call.Name, call.ArgumentsJson, succeeded));
                input.Add(AiInputItems.FunctionCallOutput(call.CallId, output));
            }
        }
    }

    private async Task<AiToolExecution> ExecuteAsync(AiFunctionCall call, AiAgentTurnRequest request, CancellationToken cancellationToken)
    {
        if (request.Sink != null)
        {
            var label = _registry.Find(call.Name) is { } tool ? SafeLabel(tool, call.ArgumentsJson) : "Checking your data";
            await request.Sink.OnStatusAsync(label, cancellationToken);
        }
        return await _executor.ExecuteAsync(call, request.ToolContext, cancellationToken);
    }

    private static string SafeLabel(IAiTool tool, string arguments)
    {
        try
        {
            return tool.ProgressLabel(AiToolArgs.Parse(arguments));
        }
        catch (AiToolArgumentException)
        {
            return tool.ProgressLabel(AiToolArgs.Empty);
        }
    }

    private static AiTokenUsage Add(AiTokenUsage left, AiTokenUsage right) => new(
        left.InputTokens + right.InputTokens,
        left.CachedTokens + right.CachedTokens,
        left.OutputTokens + right.OutputTokens,
        left.ReasoningTokens + right.ReasoningTokens);

    // Forwards provider stream events to the turn's progress sink.
    private sealed class StreamRelay(IAiAgentProgressSink sink) : IAiStreamSink
    {
        public ValueTask OnTextDeltaAsync(string delta, CancellationToken cancellationToken) =>
            sink.OnTextDeltaAsync(delta, cancellationToken);

        public ValueTask OnToolCallStartedAsync(string toolName, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
