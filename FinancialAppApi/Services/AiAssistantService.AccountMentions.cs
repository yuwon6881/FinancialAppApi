using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

// "@account" mentions. The composer lets the user pick a real ledger account instead of typing a
// nickname the assistant then has to guess at, which is the difference between "is it from CIMB to
// RYT, or are those just note labels?" and a staged draft. A mention is a *reference*, never a
// balance or a placement decision: the id is re-resolved against the tenant's own accounts here,
// and an id the client sends for an account that is archived or does not exist is dropped rather
// than trusted.
public partial class AiAssistantService
{
    internal sealed record AiResolvedAccountMention(string AccountId, string Name, string Bucket);

    private const int MaxAccountMentions = 6;
    private const int MaxAccountMentionTokenLength = 60;

    // Deliberately permissive about what follows "@": account names hold spaces and punctuation,
    // so the longest *known* account name wins against the text after the marker rather than a
    // regex trying to guess where the name ends.
    private static readonly Regex AccountMentionMarker = new(@"@(?=[\p{L}\p{N}])", RegexOptions.Compiled);

    private async Task<IReadOnlyList<AiResolvedAccountMention>> ResolveAccountMentionsAsync(
        IReadOnlyList<AiAccountMention>? clientMentions,
        string message,
        CancellationToken cancellationToken)
    {
        var hasMarker = AccountMentionMarker.IsMatch(message ?? string.Empty);
        if (!hasMarker && clientMentions is not { Count: > 0 }) return [];

        var accounts = await _context.LedgerAccounts
            .AsNoTracking()
            .Where(account => !account.IsArchived)
            .OrderBy(account => account.Name)
            .ThenBy(account => account.Id)
            .ToListAsync(cancellationToken);
        if (accounts.Count == 0) return [];

        var resolved = new List<AiResolvedAccountMention>();
        void Add(Models.LedgerAccount account)
        {
            if (resolved.Count >= MaxAccountMentions) return;
            if (resolved.Any(existing => existing.AccountId.Equals(account.Id, StringComparison.Ordinal))) return;
            resolved.Add(new AiResolvedAccountMention(account.Id, account.Name, account.Bucket));
        }

        // Message order first: "from @cimb to @ryt" is a direction, and the client's list carries
        // no ordering guarantee once a mention is edited out and retyped.
        foreach (Match marker in AccountMentionMarker.Matches(message ?? string.Empty))
        {
            var tail = message![(marker.Index + 1)..];
            var match = LongestNameMatch(tail, accounts);
            if (match != null) Add(match);
        }

        foreach (var mention in (clientMentions ?? []).Take(MaxAccountMentions * 4))
        {
            var token = (mention.Token ?? string.Empty).Trim();
            if (token.Length > MaxAccountMentionTokenLength) token = token[..MaxAccountMentionTokenLength];
            var account = mention.AccountId is { Length: > 0 } id
                ? accounts.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.Ordinal))
                : null;
            account ??= token.Length == 0 ? null : accounts.FirstOrDefault(candidate =>
                candidate.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
            if (account != null) Add(account);
        }

        return resolved;
    }

    private static Models.LedgerAccount? LongestNameMatch(string tail, IReadOnlyList<Models.LedgerAccount> accounts) =>
        accounts
            .Where(account => account.Name.Length > 0 &&
                tail.StartsWith(account.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(account => account.Name.Length)
            .FirstOrDefault();

    // The "@" is a composer affordance, not part of the request. Stripping it before intent
    // parsing keeps every existing shorthand rule working ("@CIMB Grab Mart 53" still reads as a
    // description-and-amount line, which a leading marker would have disqualified).
    internal static string StripAccountMentionMarkers(string message) =>
        string.IsNullOrEmpty(message) ? message : message.Replace("@", string.Empty, StringComparison.Ordinal);

    // A short reply carrying no amount and no record of its own ("yes, cimb to ryt") is an answer
    // to the question the previous turn asked, not a new request. Joining the two is what lets the
    // deterministic parser and the model see one complete instruction.
    internal static string CombinePendingLedgerRequest(string? pendingRequest, string message)
    {
        if (string.IsNullOrWhiteSpace(pendingRequest)) return message;
        return new StringBuilder(pendingRequest.Trim())
            .Append('\n')
            .Append(message.Trim())
            .ToString();
    }

    private static readonly Regex PendingAnswerDisqualifier = new(
        @"\?|\b(?:never ?mind|forget (?:it|that)|start over|new question|cancel)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Only carry the frame forward while the new message really is an answer. A question, an
    // explicit abandonment, or a self-contained record list of its own each start fresh --
    // otherwise a stale request would ride along on every later turn.
    internal static bool IsPendingLedgerAnswer(string? pendingRequest, string message)
    {
        if (string.IsNullOrWhiteSpace(pendingRequest) || string.IsNullOrWhiteSpace(message)) return false;
        if (PendingAnswerDisqualifier.IsMatch(message)) return false;
        return CountLedgerDraftListRecords(message) == 0;
    }

    private static readonly Regex LedgerRecordCommandSignal = new(
        @"\b(spent|spend|paid|pay|bought|buy|purchase[sd]?|transfer(?:red|s)?|move[sd]?|moving|sent|send|received|receive|got|deposit(?:ed)?|withdrew|withdrawn|withdraw|top(?:ped)? ?up|refund(?:ed)?|log|record|add)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The frame is only worth keeping when the turn was trying to stage a record and ended up
    // asking for one missing detail instead. A staged draft, or an answer with nothing left
    // outstanding, clears it -- a pending request that outlives its answer is worse than none.
    internal static string? ResolvePendingLedgerRequest(
        string effectiveMessage,
        AiChatResponse response,
        bool isLedgerAdd)
    {
        var stagedSomething = response.Actions.Any(action =>
            DraftCreatingActionTypes.Contains(action.Type));
        if (stagedSomething) return null;
        if (!response.Reply.Contains('?')) return null;
        // A question turn routinely ends in a question of its own ("want the breakdown?"), and
        // "how much did I spend" trips the verb signal on the incidental "spend". Requiring an
        // amount alongside the verb is what separates an instruction from a question about one.
        if (!isLedgerAdd &&
            !(LedgerRecordCommandSignal.IsMatch(effectiveMessage) && DraftAmountToken.IsMatch(effectiveMessage)))
        {
            return null;
        }
        var trimmed = effectiveMessage.Trim();
        return trimmed.Length == 0
            ? null
            : trimmed.Length > MaxMessageLength ? trimmed[..MaxMessageLength] : trimmed;
    }
}
