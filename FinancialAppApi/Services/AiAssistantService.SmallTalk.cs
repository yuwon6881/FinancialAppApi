using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private static readonly HashSet<string> FarewellPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "bye", "goodbye", "bye bye", "see you", "see ya", "later", "cya"
    };

    private static readonly HashSet<string> GreetingPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "hi", "hello", "hey", "hiya", "yo", "sup", "good morning", "good afternoon", "good evening"
    };

    private static readonly HashSet<string> AcknowledgmentPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "thanks", "thank you", "ty", "thx", "cheers",
        "ok", "okay", "k", "kk", "cool", "great", "nice", "awesome", "perfect",
        "got it", "sounds good", "alright", "sure", "yep", "yeah"
    };

    // Greetings/thanks/acks carry zero financial intent -- answering them never needed the
    // model at all, so this skips the AI call (and its context-building work) entirely rather
    // than just trimming what gets sent. Deliberately an exact-match closed list (after
    // stripping trailing punctuation), not a `Contains` check, so it never fires on a real
    // question that merely starts or ends with "thanks" or "ok".
    internal static bool TryHandleSmallTalk(string message, out AiChatResponse? response)
    {
        var normalized = Regex.Replace(message.Trim(), @"[!.?,]+$", "").Trim().ToLowerInvariant();

        if (FarewellPhrases.Contains(normalized))
        {
            response = new AiChatResponse("Bye! I'm here whenever you need me.", [], CloseChat: true);
            return true;
        }
        if (GreetingPhrases.Contains(normalized))
        {
            response = new AiChatResponse("Hi! Ask me a financial question or tell me what you'd like to open.", []);
            return true;
        }
        if (AcknowledgmentPhrases.Contains(normalized))
        {
            response = new AiChatResponse("Anytime! Let me know if you need anything else.", []);
            return true;
        }

        response = null;
        return false;
    }

    private static readonly Regex FollowUpSignal = new(
        @"^(and|also|what about|how about|what if|then|now|but|actually|instead|alternatively|next)\b|\b(those|these|it|them|the other|other one|same|both|either|former|latter|above|earlier|previous result|one before|one after)\b|\b(?:that|this)\b(?!\s+(?:cycle|month|year|quarter|day|week))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Biased toward keeping history: only a longer message with no continuation marker is
    // treated as a fresh, self-contained question. Short replies ("just food", "March") and
    // anything referencing "it"/"that"/"the other one" almost always depend on the prior
    // turn, so those still get history -- this only trims it for the messages least likely
    // to need it.
    private static bool NeedsHistoryContext(string message)
    {
        var trimmed = message.Trim();
        var wordCount = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return FollowUpSignal.IsMatch(trimmed) || ContinuationModifierSignal.IsMatch(trimmed)
            || IsRelativeCyclePhrase(trimmed) || ResolveRelativeDate(trimmed, "2000-01-15") != null
            || (wordCount <= 5 && !IsSelfContainedFinancialRequest(trimmed));
    }

    // A *semantic* follow-up asks about the assistant's own previous answer/conclusion ("why?", "is
    // that good?", "explain that", "should I be worried?") rather than requesting data that can be
    // reconstructed from the frame. These need the model to see the prior exchange, so a bounded
    // last user+assistant pair is sent for them (and only them).
    private static readonly Regex SemanticFollowUpSignal = new(
        @"^(?:why\b|why\?|how come\b|how so\b|really\??$|and\?$|so\?$|meaning\??$|compared to what\b|based on what\b)" +
        @"|\bis that (?:good|bad|normal|a lot|too (?:much|high|low)|ok|okay|fine|healthy|concerning|worrying|expensive|cheap)\b" +
        @"|\b(?:is|was|does|did|can|could|would) (?:that|this|it)\b.{0,45}\b(?:mean|include|exclude|matter|count|seem|make sense|affect|change|good|bad|normal|right|correct)\b" +
        @"|\bgood or bad\b|\bshould i (?:be )?(?:worry|worried|concerned)\b" +
        @"|\b(?:explain|elaborate|clarify)(?: that| this| it)?\b|\bwhat (?:do|does) (?:you|that|this|it) mean\b" +
        @"|\btell me more\b|\bexpand on (?:that|this|it)\b|\bbreak (?:that|this|it) down\b" +
        @"|\bwhat (?:caused|drove|contributed to|explains) (?:that|this|it)\b|\bwhy (?:is|was|did|does) (?:that|this|it)\b" +
        @"|\bhow did you (?:calculate|work out|derive|get) (?:that|this|it)\b|\bare you sure\b|\bwhat (?:should|can|could) i do about (?:that|this|it)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsSemanticFollowUp(string message) => SemanticFollowUpSignal.IsMatch(message.Trim());

    // Explicit "drop the context" phrasing. Kept tight (leading phrase or standalone) so it never
    // fires on an ordinary question that merely contains one of these words.
    private static readonly Regex ContextResetSignal = new(
        @"^(?:actually,?\s*)?(?:never ?mind|forget (?:that|it|about that|everything)|start over|start again|reset(?: (?:it|that|context|everything))?|clear (?:that|it|context|everything)|new (?:question|topic)|different (?:question|topic)|unrelated|change of topic|scratch that|ignore (?:that|the above|previous))(?:\s*[.!?]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool IsContextResetRequest(string message) => ContextResetSignal.IsMatch(message.Trim());

    // The minimal prior context a semantic follow-up needs: the last assistant turn (its
    // conclusion) plus the user turn that prompted it. Already sanitized/length-capped upstream.
    private static IReadOnlyList<AiChatMessage> BoundedSemanticHistory(IReadOnlyList<AiChatMessage> history) =>
        history.Count <= 2 ? history : history.TakeLast(2).ToList();

    // Compact canonical one-liner of the prior resolved frame for the intent classifier -- carries
    // enough to disambiguate a short follow-up without sending any prior message prose.
    private static string SummarizePriorFrame(AiConversationState? state)
    {
        if (state == null) return string.Empty;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.LastIntent)) parts.Add($"intent={state.LastIntent}");
        if (state.LastResolvedCycleKeys is { Count: > 0 } cycles) parts.Add($"cycles={string.Join(",", cycles)}");
        if (FormatAmountThreshold(state.LastAmountThreshold) is { } threshold) parts.Add($"threshold={threshold}");
        if (!string.IsNullOrWhiteSpace(state.LastSearchText)) parts.Add($"search={state.LastSearchText}");
        if (state.LastComparison) parts.Add("comparison=true");
        if (!string.IsNullOrWhiteSpace(state.LastTopic)) parts.Add($"topic={state.LastTopic}");
        if (state.LastQueryFacets is { Count: > 0 } facets) parts.Add($"operations={string.Join(",", facets)}");
        if (!string.IsNullOrWhiteSpace(state.LastRecurringStatus)) parts.Add($"recurringStatus={state.LastRecurringStatus}");
        return string.Join("; ", parts);
    }
}
