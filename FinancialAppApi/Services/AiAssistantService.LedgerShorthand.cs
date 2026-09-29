using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

// Recognises a message that is nothing but records to stage ("Coffee 12", "Nasi lemak 8, Grab 15").
// Such a message takes the fast path: one forced proposal round with the draft count pinned.
public partial class AiAssistantService
{
    // People do not always type one record per line. "Transfer 50 from CIMB to RYT, and spent 53
    // at Grab Mart" is two records in one sentence, and reading it as one unparseable line is how
    // a perfectly clear instruction became "send it as a description and an amount on one line".
    private static readonly Regex DraftSegmentSeparator = new(
        @"\s*(?:[,;]|\band\b)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DraftAmountToken = new(
        @"(?<![\p{L}\p{N}.])(?:rm|myr|usd|sgd|eur|gbp|aud|cad|jpy|cny|rmb|\$|€|£)\s*\d{1,9}(?:[.,]\d{1,2})?" +
        @"|(?<![\p{L}\p{N}.])\d{1,9}(?:[.,]\d{1,2})?(?![\p{L}\p{N}.])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A segment that is not in strict "description amount" shape still records money when it names
    // the movement plainly. Requiring the verb keeps a question or a bare noun phrase out.
    private static readonly Regex DraftRecordVerb = new(
        @"\b(spent|spend|paid|pay|bought|buy|purchased|transfer(?:red)?|move[ds]?|moving|sent|send|" +
        @"received|receive|got|deposit(?:ed)?|withdrew|withdrawn|top(?:ped)? ?up|refund(?:ed)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StrictDraftLine = new(
        @"^[\p{L}\p{N}][\p{L}\p{N}&'().,+/ -]{0,159}?\s+" +
        @"(?:(?:rm|myr|usd|sgd|eur|gbp|aud|cad|jpy|cny|rmb|\$|€|£)\s*)?" +
        // A single record is often typed as a sum of its parts ("Mamak 18+2.30" -- the meal plus
        // the drink). That is still one record, so it must still pin the response to one action.
        @"\d{1,9}(?:[.,]\d{1,2})?(?:\s*\+\s*\d{1,9}(?:[.,]\d{1,2})?)*" +
        @"(?:\s+(?:income|inflow|outflow|expense|refund|deposit|withdrawal|" +
        @"transfer(?:\s+from\s+[\p{L}]+\s+to\s+[\p{L}]+)?|" +
        @"essentials?|growth|stability|rewards?|[\p{L}][\p{L}-]{0,30})){0,3}\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Split only when every piece carries its own amount -- otherwise "Nasi Lemak and Teh 12"
    // would be torn into a nameless half. The whole line is not tested for validity first: a
    // three-record sentence can satisfy the single-record shape by accident, since a comma is a
    // legal description character and only the last amount has to sit near the end.
    private static IReadOnlyList<string> SplitDraftSegments(string line)
    {
        var parts = DraftSegmentSeparator.Split(line)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
        if (parts.Count < 2) return [line];
        return parts.All(part => DraftAmountToken.Matches(part).Count == 1) ? parts : [line];
    }

    internal static int CountLedgerDraftListRecords(string message)
    {
        var lines = message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .SelectMany(SplitDraftSegments)
            .ToList();
        if (lines.Count is < 1 or > AiResponseSchemas.MaxChatActions) return 0;
        if (lines.Any(line =>
                line.Contains('?') ||
                Regex.IsMatch(line, @"^(?:how|what|which|when|where|why|who|can|could|would|should|did|do|does|is|are|show|find|list)\b",
                    RegexOptions.IgnoreCase)))
        {
            return 0;
        }

        var everyLineIsADraft = lines.All(line => StrictDraftLine.IsMatch(line) ||
            (DraftAmountToken.Matches(line).Count == 1 && DraftRecordVerb.IsMatch(line)));
        return everyLineIsADraft ? lines.Count : 0;
    }
}
