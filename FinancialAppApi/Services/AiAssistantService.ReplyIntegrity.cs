using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    // A reply announcing a staged draft is a promise only the action list can keep: the client
    // stages drafts from actions and never from reply text. Actions are filtered independently of
    // the reply (sensitive mode, an unknown category, a navigation negation, or the model simply
    // omitting one), so a dropped action used to leave the user reading a confirmation for a draft
    // that was never created -- with no toast, no draft, and nothing on screen to contradict it.
    // History does not rescue it either: a past turn is replayed as text, so the next turn cannot
    // tell the draft never landed. Restate the turn honestly instead of shipping the false claim.
    private static readonly Regex DraftClaimSignal = new(
        @"\b(?:stage|staged|staging|drafted|draft|drafts|prepared|added|created|opened|ready for review)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> DraftCreatingActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openAddLedgerDraft", "openEditLedgerDraft",
        "openAddRecurringDraft", "openEditRecurringDraft",
        "openAddWishlistDraft", "openEditWishlistDraft",
        "openAddSavingsGoalDraft", "openEditSavingsGoalDraft"
    };

    // What the user's turn asked to stage. It decides whether a draft claim can be false at all,
    // and which recovery hint fits when one is.
    internal enum DraftRequest { None, LedgerAdd, OtherChange }

    // Only a turn that asked for a change can make a false claim about one. "I opened the ledger"
    // after a navigation request, or "you added 4 haircuts" inside an answer, is not a draft claim;
    // rewriting it told a user who only wanted to see their ledger that "nothing was added".
    internal static AiChatResponse EnforceActionBackedDraftClaims(
        AiChatResponse response,
        bool sensitiveMode,
        DraftRequest request = DraftRequest.LedgerAdd)
    {
        var draftCount = response.Actions.Count(action => DraftCreatingActionTypes.Contains(action.Type));
        if (draftCount > 0)
        {
            return response with
            {
                Reply = $"I prepared {draftCount} {(draftCount == 1 ? "draft" : "drafts")} for review. " +
                        "Check each one before saving."
            };
        }
        if (request == DraftRequest.None || !DraftClaimSignal.IsMatch(response.Reply)) return response;
        // "Do you want me to open a draft?" is an offer, not a claim -- the guardrail at
        // SystemInstruction already requires a clarification to carry no actions.
        if (response.Reply.Contains('?')) return response;

        return response with
        {
            Reply = sensitiveMode
                ? "Nothing was added. Unhide balances before adding a record, then send it again."
                : request == DraftRequest.LedgerAdd
                    ? "Nothing was added, so your ledger is unchanged. Send it as a description and an " +
                      "amount on one line -- for example \"Mamak 20.30\" -- and I will prepare a draft you can check."
                    : "Nothing was changed, so your records are as they were. Tell me which record and what " +
                      "to change, and I will prepare it for you to check.",
            CloseChat = false
        };
    }

    // A figure built from a capped or partial lookup must never read as exact. If a tool said its
    // result was approximate and the reply does not already hedge, the reply is marked for it.
    private static string EnforceApproximateWording(string reply, bool isApproximate)
    {
        if (!isApproximate || string.IsNullOrWhiteSpace(reply)) return reply;
        var lower = reply.ToLowerInvariant();
        var alreadyHedged = lower.Contains("approx") || lower.Contains("about ") || lower.Contains("around ")
            || lower.Contains("roughly") || lower.Contains("at least") || lower.Contains('~')
            || lower.Contains("partial") || lower.Contains("more than");
        return alreadyHedged ? reply : $"Approximately: {reply}";
    }
}
