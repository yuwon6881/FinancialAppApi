using System.Globalization;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private sealed record ConversationTurn(
        AiConversationState OutgoingState,
        List<string> Facets,
        string? ExactDate,
        string? TransactionType,
        string? LedgerCategory,
        string? RecurringStatus,
        string? WishlistStatus);

    private static ConversationTurn ResolveConversationTurn(
        AiIntentPlan intentPlan,
        AiQueryPlan queryPlan,
        TargetCycleSelection targetSelection,
        DateOnly? exactDate,
        bool sensitiveMode,
        IReadOnlyList<AiTransactionRow> allTransactions,
        IReadOnlyList<AiRecurringRow> recurringRows,
        IReadOnlyList<AiWishlistRow> wishlistRows,
        string? selectedLedgerAccountId)
    {
        var constraints = intentPlan.Constraints;
        // Outgoing conversation state: structured references the client echoes back next turn.
        // Matched transaction ids are the rows the DB actually returned this turn (re-derived,
        // never the client's claimed ids), capped, and only when this was a matching-row query.
        var matchedIds = queryPlan.TransactionData == TransactionDataLevel.MatchingRows && !string.IsNullOrWhiteSpace(queryPlan.SearchText)
            ? allTransactions.Take(MaxStateMatchedIds).Select(t => t.Id).ToList()
            : null;
        var resolvedCycleLabel = targetSelection.Cycles.Count > 0
            ? $"{FinancialConstants.MonthAbbreviations[targetSelection.Cycles[0].MonthIndex - 1]} {targetSelection.Cycles[0].Year}"
            : null;
        int? resolvedWishlistItemId = wishlistRows.Count == 1
            ? wishlistRows[0].Id
            : wishlistRows.Count(w => w.IsActive && !w.IsPurchased) == 1
                ? wishlistRows.First(w => w.IsActive && !w.IsPurchased).Id
                : null;
        // Resolve this turn's frame dimensions so the next follow-up can inherit them. Each falls
        // back to the base (prior) value when not in play this turn, giving "sticky" context that a
        // later turn's explicit value overrides (and read-side family scoping stops an incompatible
        // turn from consuming it).
        var turnCycleKeys = targetSelection.Cycles.Count > 0
            ? targetSelection.Cycles.Select(FormatCycleKey).ToList()
            : null;
        var turnThreshold = TryParseAmountThreshold(queryPlan.QueryText) is { } parsedThreshold
            ? ToWireThreshold(parsedThreshold)
            : null;
        var turnExactDate = exactDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var turnTransactionType = DetectTransactionType(queryPlan.QueryText);
        var isRecurringTurn = intentPlan.Intents.Any(i => i is AiIntent.RecurringList or AiIntent.RecurringUpcoming or AiIntent.RecurringAdd or AiIntent.RecurringEdit);
        var isWishlistTurn = intentPlan.Intents.Any(i => i is AiIntent.WishlistList or AiIntent.WishlistForecast or AiIntent.WishlistAdd or AiIntent.WishlistEdit);
        var isTransactionalTurn = !isRecurringTurn && !isWishlistTurn;
        var turnFacets = DetectQueryFacets(queryPlan.QueryText).Where(KnownQueryFacets.Contains).Take(12).ToList();
        var turnIntentNames = intentPlan.Intents.Select(ToIntentName).ToList();
        var turnTopic = DetermineConversationTopic(queryPlan.QueryText, turnIntentNames, intentPlan.ConversationState)
            ?? intentPlan.ConversationState.LastTopic;
        var turnRecurringStatus = DetectRecurringStatus(queryPlan.QueryText);
        var turnWishlistStatus = DetectWishlistStatus(queryPlan.QueryText);
        var turnLedgerCategory = intentPlan.Entities?.LedgerCategory ?? DetectLedgerCategory(queryPlan.QueryText);
        var turnForecast = TryParseLedgerBalanceForecast(queryPlan.QueryText);
        var mentionedRecurringReference = FindMentionedEntityName(queryPlan.QueryText, recurringRows.Select(r => r.Name));
        var mentionedWishlistReference = FindMentionedEntityName(queryPlan.QueryText, wishlistRows.Select(w => w.Name));
        // A threshold-only question ("which transaction exceeded 100") has no searchText, so its
        // matching rows weren't captured above -- capture them here so a referential follow-up
        // ("which of those was the biggest?") can resolve against them.
        var thresholdMatchedIds = matchedIds == null && !sensitiveMode && ToInternalThreshold(turnThreshold) is { } activeThreshold
            ? allTransactions
                .Where(t => t.Amount < 0 && TransactionReportSemantics.IsReportableCashMovement(t.Amount, t.Category, t.LedgerCategory) && MatchesThreshold(Math.Abs(t.Amount), activeThreshold))
                .Take(MaxStateMatchedIds).Select(t => t.Id).ToList()
            : null;
        if (thresholdMatchedIds is { Count: 0 }) thresholdMatchedIds = null;
        // Filter-lifting follow-ups drop the sticky exclusions instead of carrying them forever.
        // (queryPlan.QueryText contains the original message; expansion only appends, so these
        // phrases are still detectable and the appended canonical clauses never trip them.)
        var clearsFilters = WantsClearFilters(queryPlan.QueryText);
        var includesTransfers = WantsIncludeTransfers(queryPlan.QueryText);
        var clearsAmountFilter = WantsClearAmountFilter(queryPlan.QueryText);
        var clearsSearch = WantsClearSearch(queryPlan.QueryText);
        var baseState = intentPlan.ConversationState;
        var outgoingState = baseState with
        {
            LastSearchText = clearsSearch ? null : queryPlan.SearchText ?? baseState.LastSearchText,
            LastCycleHint = queryPlan.CycleHint ?? baseState.LastCycleHint,
            LastResolvedCycle = resolvedCycleLabel ?? baseState.LastResolvedCycle,
            LastMatchedTransactionIds = matchedIds ?? thresholdMatchedIds ?? baseState.LastMatchedTransactionIds,
            LastWishlistItemId = queryPlan.WishlistItemId.HasValue
                ? resolvedWishlistItemId
                : resolvedWishlistItemId ?? baseState.LastWishlistItemId,
            LastResolvedCycleKeys = turnCycleKeys ?? baseState.LastResolvedCycleKeys,
            LastAmountThreshold = clearsAmountFilter ? null : turnThreshold ?? baseState.LastAmountThreshold,
            LastExcludeTransfers = !includesTransfers && (constraints.ExcludeTransfers || baseState.LastExcludeTransfers),
            LastExcludedCategories = clearsFilters
                ? null
                : constraints.ExcludedCategories is { Count: > 0 }
                    ? constraints.ExcludedCategories
                    : baseState.LastExcludedCategories,
            LastIncludedCategories = clearsFilters
                ? null
                : constraints.IncludedCategories is { Count: > 0 }
                    ? constraints.IncludedCategories
                    : baseState.LastIncludedCategories,
            LastLedgerCategory = turnLedgerCategory ?? baseState.LastLedgerCategory,
            LastCategory = intentPlan.Entities?.Category ?? baseState.LastCategory,
            LastTransactionType = turnTransactionType ?? baseState.LastTransactionType,
            LastExactDate = isTransactionalTurn ? turnExactDate : baseState.LastExactDate,
            LastComparison = isTransactionalTurn ? queryPlan.NeedsCycleComparison : baseState.LastComparison,
            LastRecurringReference = isRecurringTurn
                ? mentionedRecurringReference ?? queryPlan.SearchText ?? baseState.LastRecurringReference
                : baseState.LastRecurringReference,
            LastWishlistReference = isWishlistTurn
                ? mentionedWishlistReference ?? baseState.LastWishlistReference
                : baseState.LastWishlistReference,
            LastTopic = turnTopic,
            LastQueryFacets = turnFacets.Count > 0 ? turnFacets : null,
            LastRecurringStatus = isRecurringTurn ? turnRecurringStatus : baseState.LastRecurringStatus,
            LastWishlistStatus = isWishlistTurn ? turnWishlistStatus : baseState.LastWishlistStatus,
            LastTargetAmount = isTransactionalTurn ? turnForecast?.Target : baseState.LastTargetAmount,
            LastRewardsTopic = queryPlan.NeedsRewards ? "plan" : baseState.LastRewardsTopic,
            LastSavingsGoalId = intentPlan.ConversationState.LastSavingsGoalId ?? baseState.LastSavingsGoalId,
            LastInvestmentTopic = queryPlan.NeedsInvestments ? "portfolio" : baseState.LastInvestmentTopic,
            LastInvestmentRange = intentPlan.ConversationState.LastInvestmentRange ?? baseState.LastInvestmentRange,
            LastInvestmentInstrumentId = intentPlan.ConversationState.LastInvestmentInstrumentId ?? baseState.LastInvestmentInstrumentId,
            LastReportCycleKey = queryPlan.NeedsReport && targetSelection.Cycles.Count == 1
                ? ToCycleKey(targetSelection.Cycles[0])
                : intentPlan.ConversationState.LastReportCycleKey ?? baseState.LastReportCycleKey,
            LastLedgerAccountId = selectedLedgerAccountId ?? baseState.LastLedgerAccountId
        };

        return new ConversationTurn(
            outgoingState,
            turnFacets,
            turnExactDate,
            turnTransactionType,
            turnLedgerCategory,
            turnRecurringStatus,
            turnWishlistStatus);
    }
}
