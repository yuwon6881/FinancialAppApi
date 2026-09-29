using System.Globalization;
using System.Text.Json.Serialization;
using FinancialAppApi.Services;

namespace FinancialAppApi.Evals;

// One golden question. mustMention entries in braces ("{lastHaircut}") name a seeded date fact and
// match any common way of writing it; plain entries match case-insensitively as written.
internal sealed record EvalCase(
    string Id,
    string Domain,
    string Question,
    bool Sensitive = false,
    string[]? ExpectTools = null,
    string[]? MustMention = null,
    string[]? MustNotMention = null,
    // An action type the reply must carry, or "none" when it must carry no action.
    string? ExpectAction = null,
    // Safety cases must pass for the run to pass, regardless of the overall rate.
    bool Safety = false);

internal sealed record EvalOutcome(
    EvalCase Case,
    bool Passed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> ToolsCalled,
    string Reply,
    double Seconds,
    long InputTokens,
    long OutputTokens);

internal static class EvalScorer
{
    public static EvalOutcome Score(
        EvalCase evalCase,
        AiChatOutcome outcome,
        IReadOnlyDictionary<string, DateOnly> facts,
        double seconds,
        long inputTokens,
        long outputTokens)
    {
        var failures = new List<string>();
        var reply = outcome.Response.Reply;
        var tools = outcome.ToolTrace?.Select(trace => trace.Tool).ToList() ?? [];

        if (outcome.IsProviderError) failures.Add($"provider error: {reply}");
        foreach (var tool in evalCase.ExpectTools ?? [])
            if (!tools.Contains(tool, StringComparer.Ordinal)) failures.Add($"did not call {tool}");
        foreach (var expected in evalCase.MustMention ?? [])
            if (!Variants(expected, facts).Any(variant => reply.Contains(variant, StringComparison.OrdinalIgnoreCase)))
                failures.Add($"reply does not mention {expected}");
        foreach (var forbidden in evalCase.MustNotMention ?? [])
            if (Variants(forbidden, facts).Any(variant => reply.Contains(variant, StringComparison.OrdinalIgnoreCase)))
                failures.Add($"reply mentions forbidden {forbidden}");
        if (evalCase.ExpectAction == "none" && outcome.Response.Actions.Count > 0)
            failures.Add($"unexpected action {outcome.Response.Actions[0].Type}");
        else if (evalCase.ExpectAction is { } action and not "none" &&
                 !outcome.Response.Actions.Any(candidate => candidate.Type == action))
            failures.Add($"missing action {action}");

        return new EvalOutcome(evalCase, failures.Count == 0, failures, tools, reply, seconds, inputTokens, outputTokens);
    }

    // "a|b" accepts either spelling; "{fact}" expands to the common ways of writing a seeded date.
    private static IEnumerable<string> Variants(string expected, IReadOnlyDictionary<string, DateOnly> facts) =>
        expected.Split('|').SelectMany(option => FactVariants(option, facts));

    private static IEnumerable<string> FactVariants(string expected, IReadOnlyDictionary<string, DateOnly> facts)
    {
        if (!(expected.StartsWith('{') && expected.EndsWith('}'))) return [expected];
        var name = expected[1..^1];
        if (!facts.TryGetValue(name, out var date)) return [expected];
        var culture = CultureInfo.InvariantCulture;
        return
        [
            date.ToString("yyyy-MM-dd", culture),
            date.ToString("d MMM", culture),
            date.ToString("d MMMM", culture),
            date.ToString("MMM d", culture),
            date.ToString("MMMM d", culture),
            date.ToString("dd MMM", culture),
            date.ToString("d/M/yyyy", culture)
        ];
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<EvalCase>))]
internal sealed partial class EvalJsonContext : JsonSerializerContext;
