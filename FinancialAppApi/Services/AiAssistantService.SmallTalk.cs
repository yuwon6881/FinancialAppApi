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

    // Explicit "drop the context" phrasing. Kept tight (leading phrase or standalone) so it never
    // fires on an ordinary question that merely contains one of these words.
    private static readonly Regex ContextResetSignal = new(
        @"^(?:actually,?\s*)?(?:never ?mind|forget (?:that|it|about that|everything)|start over|start again|reset(?: (?:it|that|context|everything))?|clear (?:that|it|context|everything)|new (?:question|topic)|different (?:question|topic)|unrelated|change of topic|scratch that|ignore (?:that|the above|previous))(?:\s*[.!?]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool IsContextResetRequest(string message) => ContextResetSignal.IsMatch(message.Trim());
}
