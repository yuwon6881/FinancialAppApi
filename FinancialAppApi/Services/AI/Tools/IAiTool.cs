using System.Text.Json.Nodes;

namespace FinancialAppApi.Services.AI.Tools;

// How a tool behaves in sensitive mode. MasksAmounts still runs and has every money-bearing
// property stripped from its result; HiddenWhenSensitive does not run at all because its whole
// answer is a financial figure (a portfolio value, a forecast) with nothing honest left to show.
public enum AiToolSensitivity
{
    None,
    MasksAmounts,
    HiddenWhenSensitive
}

// A read-only capability the model may call. Tools never mutate data: every change still goes
// through a proposed UI action that the user confirms.
public interface IAiTool
{
    string Name { get; }
    string Description { get; }
    JsonObject ParametersSchema { get; }
    AiToolSensitivity Sensitivity { get; }

    // Money-bearing property names this tool emits beyond AiSensitiveMasker.MoneyProperties.
    IReadOnlySet<string> AmountProperties => EmptyProperties;

    // Plain-language progress shown while the tool runs ("Searching your transactions for ...").
    string ProgressLabel(AiToolArgs args);

    Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken);

    private static readonly IReadOnlySet<string> EmptyProperties = new HashSet<string>();
}

// Data is serialized for the model as-is. Truncated and Approximate are surfaced beside it so
// the model can say "approximately" instead of presenting a partial figure as complete.
public sealed record AiToolResult(object? Data, bool Truncated = false, bool Approximate = false)
{
    public static AiToolResult Of(object? data, bool truncated = false, bool approximate = false) =>
        new(data, truncated, approximate);
}

// A caller mistake the model can correct on its next round (unknown cycle, bad date, ...).
// The message is shown to the model verbatim, so it names the accepted values.
public sealed class AiToolArgumentException(string message) : Exception(message);
