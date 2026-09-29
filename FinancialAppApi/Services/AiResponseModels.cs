using System.Text.Json.Nodes;

namespace FinancialAppApi.Services;

// A function the model may call. Parameters is a JSON schema object; Strict opts into the
// provider's exact-schema mode, which requires every property to be listed as required.
public sealed record AiFunctionTool(string Name, string Description, JsonObject Parameters, bool Strict = false);

// One provider round. Input items are raw Responses API items rather than a closed type
// hierarchy because reasoning and function-call items returned by the provider must be echoed
// back verbatim on the next round -- with store=false that is the only way reasoning survives.
public sealed record AiResponseRequest(
    string Feature,
    IReadOnlyList<JsonObject> Input,
    int MaxOutputTokens,
    string ModelConfigurationKey,
    string? Instructions = null,
    IReadOnlyList<AiFunctionTool>? Tools = null,
    // null omits the field; "auto" | "none" | "required", or any other value names one function.
    string? ToolChoice = null,
    bool ParallelToolCalls = true,
    string? ReasoningEffort = "low",
    object? OutputJsonSchema = null,
    string? PromptCacheKey = null,
    TimeSpan? Timeout = null);

public sealed record AiFunctionCall(string CallId, string Name, string ArgumentsJson);

public sealed record AiTokenUsage(int InputTokens, int CachedTokens, int OutputTokens, int ReasoningTokens)
{
    public static readonly AiTokenUsage None = new(0, 0, 0, 0);
}

public sealed record AiResponseResult(
    string Model,
    string Text,
    IReadOnlyList<AiFunctionCall> FunctionCalls,
    // Every output item, in provider order, ready to append to the next round's input.
    IReadOnlyList<JsonObject> OutputItems,
    AiTokenUsage Usage);

// Receives incremental provider events while a streamed round is in flight. Final state is
// still delivered through AiResponseResult; the sink only drives live progress.
public interface IAiStreamSink
{
    ValueTask OnTextDeltaAsync(string delta, CancellationToken cancellationToken);
    ValueTask OnToolCallStartedAsync(string toolName, CancellationToken cancellationToken);
}

public static class AiInputItems
{
    public static JsonObject Message(string role, string text) => new()
    {
        ["role"] = role,
        ["content"] = text
    };

    // Used to replay a call the server executed on the model's behalf (a preset's seeded tool).
    public static JsonObject FunctionCall(string callId, string name, string argumentsJson) => new()
    {
        ["type"] = "function_call",
        ["call_id"] = callId,
        ["name"] = name,
        ["arguments"] = argumentsJson
    };

    public static JsonObject FunctionCallOutput(string callId, string output) => new()
    {
        ["type"] = "function_call_output",
        ["call_id"] = callId,
        ["output"] = output
    };
}
