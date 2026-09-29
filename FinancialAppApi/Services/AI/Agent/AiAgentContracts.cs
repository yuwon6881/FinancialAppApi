using System.Text.Json.Nodes;
using FinancialAppApi.Services.AI.Tools;

namespace FinancialAppApi.Services.AI.Agent;

// Live progress for one turn. Status labels and text deltas are provisional: the final reply is
// authoritative and may differ (a staged-draft reply is rewritten by the server), so a client
// always replaces streamed text with the completed answer.
public interface IAiAgentProgressSink
{
    ValueTask OnStatusAsync(string label, CancellationToken cancellationToken);
    ValueTask OnTextDeltaAsync(string delta, CancellationToken cancellationToken);

    // The model wrote text and then decided to call tools; that text was thinking out loud, not
    // the answer, so the client drops it.
    ValueTask OnTextResetAsync(CancellationToken cancellationToken);
}

// Validates the UI actions the model proposes. Implemented by the chat service, which owns the
// action rules; the engine only relays the verdict back to the model.
public interface IAiActionProposer
{
    JsonObject ParametersSchema { get; }

    // Returns the model-readable verdict for this call; accepted actions accumulate.
    Task<string> ProposeAsync(string argumentsJson, CancellationToken cancellationToken);

    IReadOnlyList<AiUiAction> Accepted { get; }
}

// A call the server makes on the model's behalf before the first round (a preset's obvious
// lookup), replayed into the conversation as if the model had asked for it.
public sealed record AiSeededToolCall(string ToolName, string ArgumentsJson);

public sealed record AiAgentTurnRequest(
    string UserMessage,
    // Earlier dialogue, the developer snapshot, and anything else that precedes the user message.
    IReadOnlyList<JsonObject> PriorInput,
    AiToolContext ToolContext,
    IAiActionProposer ActionProposer,
    IReadOnlyList<AiSeededToolCall>? SeededCalls = null,
    // Forces the first round to call this tool (the ledger-shorthand fast path).
    string? ForcedFirstTool = null,
    IAiAgentProgressSink? Sink = null);

public sealed record AiToolTrace(string Tool, string Arguments, bool Succeeded);

public sealed record AiAgentTurnResult(
    string Reply,
    IReadOnlyList<AiUiAction> Actions,
    IReadOnlyList<AiToolTrace> Trace,
    AiTokenUsage Usage,
    int Rounds,
    bool AnyApproximate,
    bool HitToolLimit);
