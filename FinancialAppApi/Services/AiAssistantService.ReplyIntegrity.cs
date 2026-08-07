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
        @"\b(?:stage|staged|staging|drafted|draft|drafts)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> DraftCreatingActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openAddLedgerDraft", "openEditLedgerDraft",
        "openAddRecurringDraft", "openEditRecurringDraft",
        "openAddWishlistDraft", "openEditWishlistDraft",
        "openAddSavingsGoalDraft", "openEditSavingsGoalDraft"
    };

    internal static AiChatResponse EnforceActionBackedDraftClaims(AiChatResponse response, bool sensitiveMode)
    {
        if (response.Actions.Any(action => DraftCreatingActionTypes.Contains(action.Type))) return response;
        if (!DraftClaimSignal.IsMatch(response.Reply)) return response;
        // "Do you want me to open a draft?" is an offer, not a claim -- the guardrail at
        // SystemInstruction already requires a clarification to carry no actions.
        if (response.Reply.Contains('?')) return response;

        return response with
        {
            Reply = sensitiveMode
                ? "Nothing was added. Unhide balances before adding a record, then send it again."
                : "Nothing was added, so your ledger is unchanged. Send it as a description and an " +
                  "amount on one line -- for example \"Mamak 20.30\" -- and I will prepare a draft you can check.",
            CloseChat = false
        };
    }
}
