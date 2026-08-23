using System.Globalization;
using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    // Builds the base conversation frame for this turn: this turn's resolved intent/search/cycle,
    // with every other dimension carried forward from the prior frame (BuildContextAsync then
    // overrides the dimensions actually in play this turn with their freshly-resolved values). No
    // longer reads the prior message text -- a referential follow-up ("which of those") is detected
    // from the CURRENT message via UsesPriorTransactionState, and the prior frame supplies the ids.
    private static AiConversationState ResolveConversationState(
        string message,
        IReadOnlyList<string> intents,
        string? searchText,
        string? cycleHint,
        AiConversationState? priorState = null)
    {
        var carriesTransactionState = UsesPriorTransactionState(message);
        var resolvedIntents = intents.Where(i => !i.Equals("general", StringComparison.OrdinalIgnoreCase)).ToList();
        var topic = DetermineConversationTopic(message, resolvedIntents, priorState) ?? priorState?.LastTopic;
        var continuation = priorState != null && NeedsHistoryContext(message);
        // A self-contained question in a family starts a new frame for that family. A switch to a
        // different family may keep the dormant transaction frame so a later explicit cycle-only
        // continuation can return to it (the existing transaction -> wishlist -> cycle behavior).
        var carryTransactionFrame = priorState != null && (continuation || topic != TransactionTopic);
        var carryWishlistFrame = priorState != null && (continuation || topic != WishlistTopic);
        var carryRecurringFrame = priorState != null && (continuation || topic != RecurringTopic);
        return new AiConversationState(
            intents.FirstOrDefault(i => !i.Equals("general", StringComparison.OrdinalIgnoreCase)) ?? priorState?.LastIntent,
            searchText ?? (carryTransactionFrame ? priorState?.LastSearchText : null),
            cycleHint ?? (carryTransactionFrame ? priorState?.LastCycleHint : null),
            ExtractWishlistReference(message) ?? (carryWishlistFrame ? priorState?.LastWishlistReference : null),
            carryTransactionFrame ? priorState?.LastResolvedCycle : null,
            carryTransactionFrame && carriesTransactionState ? priorState?.LastMatchedTransactionIds : null,
            carryWishlistFrame && carriesTransactionState ? priorState?.LastWishlistItemId : null,
            carryTransactionFrame ? priorState?.LastCategory : null,
            carryTransactionFrame ? priorState?.LastResolvedCycleKeys : null,
            carryTransactionFrame ? priorState?.LastAmountThreshold : null,
            carryTransactionFrame && priorState?.LastExcludeTransfers == true,
            carryTransactionFrame ? priorState?.LastExcludedCategories : null,
            carryTransactionFrame ? priorState?.LastIncludedCategories : null,
            carryTransactionFrame ? priorState?.LastLedgerCategory : null,
            carryTransactionFrame ? priorState?.LastTransactionType : null,
            MessageMentionsCycle(message) && !MessageMentionsExactDate(message)
                ? null
                : carryTransactionFrame ? priorState?.LastExactDate : null,
            carryTransactionFrame && priorState?.LastComparison == true,
            carryRecurringFrame ? priorState?.LastRecurringReference : null,
            // This turn's resolved intents become the frame's intent set (BuildContextAsync leaves
            // this as-is); a non-general set here is what a later follow-up inherits its analysis
            // from.
            resolvedIntents.Count > 0 ? resolvedIntents : priorState?.LastIntents,
            topic,
            continuation ? priorState?.LastQueryFacets : null,
            carryRecurringFrame ? priorState?.LastRecurringStatus : null,
            carryWishlistFrame ? priorState?.LastWishlistStatus : null,
            carryTransactionFrame ? priorState?.LastTargetAmount : null,
            priorState?.LastRewardsTopic,
            priorState?.LastSavingsGoalId,
            priorState?.LastInvestmentTopic,
            priorState?.LastInvestmentRange,
            priorState?.LastInvestmentInstrumentId,
            priorState?.LastReportCycleKey,
            priorState?.LastLoanId,
            priorState?.LastLedgerAccountId);
    }

    private static string? ExtractConversationCycle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (AllHistorySignal.IsMatch(text)) return "all history";
        var match = Regex.Match(text, @"\b((?:last|previous|prior|this|current)\s+(?:\d+\s+|few\s+)?(?:cycle|cycles|month|months))\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractWishlistReference(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"\b(?:wishlist|wish list|saving for|afford)\s+([\p{L}\p{N}][\p{L}\p{N}'& -]{1,60})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // The `history` array is client-supplied on every request (this endpoint is stateless),
    // so it must not be trusted as-is: an unbounded role string or message length would let a
    // caller smuggle fabricated "instructions" into what the model is told is prior
    // conversation. Restrict to the two real roles and cap length like any other input.
    private static IReadOnlyList<AiChatMessage> SanitizeHistory(IReadOnlyList<AiChatMessage>? history)
    {
        if (history == null || history.Count == 0)
        {
            return [];
        }

        return history
            .TakeLast(12)
            .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m with { Content = m.Content.Length > MaxHistoryMessageLength ? m.Content[..MaxHistoryMessageLength] : m.Content })
            .ToList();
    }

    // The canonical intent vocabulary. Also used to reject unknown/tampered intent strings
    // arriving on client-carried conversation state or classifier output.
    private static bool IsKnownIntentName(string name) => IntentByName.ContainsKey(name);

    private const int MaxStateSearchLength = 80;
    private const int MaxStateMatchedIds = 50;

    // Client-carried conversation state is untrusted input, exactly like history. An unknown
    // intent, an over-long search string, or a giant id list must never flow into query
    // building unchecked. IDs here are only *hints*; they are re-derived/re-validated against
    // the DB before being surfaced again, never trusted verbatim.
    internal static AiConversationState? SanitizeConversationState(AiConversationState? state)
    {
        if (state == null) return null;
        var intent = !string.IsNullOrWhiteSpace(state.LastIntent) && IsKnownIntentName(state.LastIntent)
            ? state.LastIntent.ToLowerInvariant()
            : null;
        string? Clamp(string? value) => string.IsNullOrWhiteSpace(value)
            ? null
            : (value.Length > MaxStateSearchLength ? value[..MaxStateSearchLength] : value).Trim();
        var matchedIds = state.LastMatchedTransactionIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Length > 64 ? id[..64] : id)
            .Take(MaxStateMatchedIds)
            .ToList();
        // Resolved-frame fields are untrusted too. Cycle keys must be canonical "yyyy-MM" in a sane
        // range; the threshold must round-trip through the canonical parser (rejecting anything the
        // parser can't read); excluded categories are clamped/capped; the ledger category must be a
        // real one.
        var cycleKeys = state.LastResolvedCycleKeys?
            .Where(IsValidCycleKey)
            .Distinct()
            .Take(24)
            .ToList();
        // Round-trip the typed threshold through the internal validator (rejects a bad comparator
        // name or out-of-range amount).
        var threshold = ToInternalThreshold(state.LastAmountThreshold) is { } internalThreshold
            ? ToWireThreshold(internalThreshold)
            : null;
        IReadOnlyList<string>? CleanCategoryList(IReadOnlyList<string>? list) => list?
            .Select(Clamp)
            .Where(c => c != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList() is { Count: > 0 } cleaned ? cleaned : null;
        var excludedCategories = CleanCategoryList(state.LastExcludedCategories);
        var includedCategories = CleanCategoryList(state.LastIncludedCategories);
        var ledgerCategory = !string.IsNullOrWhiteSpace(state.LastLedgerCategory)
            && LedgerCategories.Contains(state.LastLedgerCategory, StringComparer.OrdinalIgnoreCase)
            ? LedgerCategories.First(c => c.Equals(state.LastLedgerCategory, StringComparison.OrdinalIgnoreCase))
            : null;
        var transactionType = state.LastTransactionType is "income" or "inflow" or "outflow" or "transfer"
            ? state.LastTransactionType
            : null;
        var exactDate = DateOnly.TryParseExact(state.LastExactDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? state.LastExactDate
            : null;
        var intents = state.LastIntents?
            .Where(i => !string.IsNullOrWhiteSpace(i) && IsKnownIntentName(i))
            .Select(i => i.ToLowerInvariant())
            .Distinct()
            .Take(6)
            .ToList();
        var topic = !string.IsNullOrWhiteSpace(state.LastTopic) && KnownConversationTopics.Contains(state.LastTopic)
            ? state.LastTopic
            : null;
        var facets = state.LastQueryFacets?
            .Where(f => !string.IsNullOrWhiteSpace(f) && KnownQueryFacets.Contains(f))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();
        var recurringStatus = state.LastRecurringStatus is "discarded" or "pending" or "paid" or "inactive" or "active"
            ? state.LastRecurringStatus
            : null;
        var wishlistStatus = state.LastWishlistStatus is "unpurchased" or "purchased" or "affordable" or "inactive" or "active"
            ? state.LastWishlistStatus
            : null;
        var targetAmount = state.LastTargetAmount is > 0m and <= 1_000_000_000m ? state.LastTargetAmount : null;
        var rewardsTopic = state.LastRewardsTopic is "plan" ? state.LastRewardsTopic : null;
        var savingsGoalId = state.LastSavingsGoalId is > 0 ? state.LastSavingsGoalId : null;
        var investmentTopic = state.LastInvestmentTopic is "portfolio" ? state.LastInvestmentTopic : null;
        var investmentRange = state.LastInvestmentRange is "1m" or "3m" or "6m" or "1y" or "3y" or "5y" or "all"
            ? state.LastInvestmentRange
            : null;
        Guid? investmentInstrumentId = state.LastInvestmentInstrumentId is { } instrumentId && instrumentId != Guid.Empty
            ? instrumentId
            : null;
        var reportCycleKey = IsValidCycleKey(state.LastReportCycleKey) ? state.LastReportCycleKey : null;
        return new AiConversationState(
            intent,
            Clamp(state.LastSearchText),
            Clamp(state.LastCycleHint),
            Clamp(state.LastWishlistReference),
            Clamp(state.LastResolvedCycle),
            matchedIds is { Count: > 0 } ? matchedIds : null,
            state.LastWishlistItemId is > 0 ? state.LastWishlistItemId : null,
            Clamp(state.LastCategory),
            cycleKeys is { Count: > 0 } ? cycleKeys : null,
            threshold,
            state.LastExcludeTransfers,
            excludedCategories,
            includedCategories,
            ledgerCategory,
            transactionType,
            exactDate,
            state.LastComparison,
            Clamp(state.LastRecurringReference),
            intents is { Count: > 0 } ? intents : null,
            topic,
            facets is { Count: > 0 } ? facets : null,
            recurringStatus,
            wishlistStatus,
            targetAmount,
            rewardsTopic,
            savingsGoalId,
            investmentTopic,
            investmentRange,
            investmentInstrumentId,
            reportCycleKey,
            Clamp(state.LastLoanId),
            Clamp(state.LastLedgerAccountId),
            // The carried request is the user's own earlier words, so it is bounded by the same
            // ceiling their message is and never trusted for anything but re-parsing.
            string.IsNullOrWhiteSpace(state.PendingLedgerRequest)
                ? null
                : state.PendingLedgerRequest.Trim() is { Length: > MaxMessageLength } tooLong
                    ? tooLong[..MaxMessageLength]
                    : state.PendingLedgerRequest.Trim());
    }

}
