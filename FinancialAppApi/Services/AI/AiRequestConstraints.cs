using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

// The two parts of a request action validation must honour exactly rather than leave to the
// model: "don't open the ledger" drops navigation, and "what if I..." is a question, never an
// edit. Read from the user's own words only.
public partial class AiAssistantService
{
    internal sealed record AiConstraints(bool PreventNavigation, bool Hypothetical)
    {
        public static readonly AiConstraints None = new(false, false);
    }

    // "don't/do not ... open/show/go to/navigate/take me" or an explicit "without opening".
    private static readonly Regex PreventNavigationSignal = new(
        @"\b(?:don'?t|do not|no need to|please don'?t|without)\b[^.?!]{0,40}?\b(open|show me|go to|navigate|take me|switch to)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HypotheticalSignal = new(
        @"\b(what if|suppose|assuming|hypothetically|imagine|if i (?:save|saved|earn|earned|make|made|had|invest|invested|spend|spent|cut|increase|increased|reduce|reduced)|if my \w+ (?:increase|increases|goes up|drops|drop|rises|falls|changes))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static AiConstraints ParseConstraints(string message) =>
        string.IsNullOrWhiteSpace(message)
            ? AiConstraints.None
            : new AiConstraints(PreventNavigationSignal.IsMatch(message), HypotheticalSignal.IsMatch(message));
}
