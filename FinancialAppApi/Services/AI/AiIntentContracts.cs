namespace FinancialAppApi.Services;

// Phase 1: the typed intent surface. The pipeline historically routed on dotted string names
// ("ledger.activity_count") because the classifier JSON schema speaks those strings. This
// layer gives every one of them a strongly-typed AiIntent, plus typed entity/resolution
// records, so callers and tests can reason about intents without stringly-typed comparisons.
// String <-> enum conversion is total and lossless (Unknown for anything off-vocabulary).
public partial class AiAssistantService
{
    public enum AiIntent
    {
        Unknown,
        General,
        Navigation,
        LedgerActivityCount,
        LedgerMerchantSearch,
        LedgerSpendingTotal,
        LedgerTransactionList,
        LedgerComparison,
        LedgerAdd,
        LedgerEdit,
        LedgerAnomaly,
        LedgerDuplicates,
        WishlistList,
        WishlistForecast,
        WishlistAdd,
        WishlistEdit,
        RecurringList,
        RecurringUpcoming,
        RecurringAdd,
        RecurringEdit,
        AllocationBalance,
        AllocationPerformance
    }

    private static readonly Dictionary<string, AiIntent> IntentByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general"] = AiIntent.General,
        ["navigation"] = AiIntent.Navigation,
        ["ledger.activity_count"] = AiIntent.LedgerActivityCount,
        ["ledger.merchant_search"] = AiIntent.LedgerMerchantSearch,
        ["ledger.spending_total"] = AiIntent.LedgerSpendingTotal,
        ["ledger.transaction_list"] = AiIntent.LedgerTransactionList,
        ["ledger.comparison"] = AiIntent.LedgerComparison,
        ["ledger.add"] = AiIntent.LedgerAdd,
        ["ledger.edit"] = AiIntent.LedgerEdit,
        ["ledger.anomaly"] = AiIntent.LedgerAnomaly,
        ["ledger.duplicates"] = AiIntent.LedgerDuplicates,
        ["wishlist.list"] = AiIntent.WishlistList,
        ["wishlist.forecast"] = AiIntent.WishlistForecast,
        ["wishlist.add"] = AiIntent.WishlistAdd,
        ["wishlist.edit"] = AiIntent.WishlistEdit,
        ["recurring.list"] = AiIntent.RecurringList,
        ["recurring.upcoming"] = AiIntent.RecurringUpcoming,
        ["recurring.add"] = AiIntent.RecurringAdd,
        ["recurring.edit"] = AiIntent.RecurringEdit,
        ["allocation.balance"] = AiIntent.AllocationBalance,
        ["allocation.performance"] = AiIntent.AllocationPerformance
    };

    private static readonly Dictionary<AiIntent, string> NameByIntent =
        IntentByName.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    internal static AiIntent ParseIntent(string? name) =>
        name != null && IntentByName.TryGetValue(name, out var intent) ? intent : AiIntent.Unknown;

    internal static string ToIntentName(AiIntent intent) =>
        NameByIntent.TryGetValue(intent, out var name) ? name : "general";

    internal static IReadOnlyList<AiIntent> ParseIntents(IEnumerable<string> names) =>
        names.Select(ParseIntent).Where(i => i != AiIntent.Unknown).Distinct().ToList();

    // Typed entities extracted from a request (or carried by conversation state). These are the
    // values that directly parameterize database queries downstream (search text, cycle,
    // category, amount, referenced record ids).
    internal sealed record AiIntentEntities(
        string? SearchText,
        string? CycleHint,
        DateOnly? Date,
        string? Category,
        string? LedgerCategory,
        decimal? Amount,
        int? WishlistItemId,
        string? WishlistReference,
        string? TransactionReference,
        IReadOnlyList<string> TransactionIds,
        IReadOnlyList<string> Exclusions);

    // One authoritative resolution: the intents (typed), the entities, the constraints, a
    // confidence, whether the classifier was consulted, and any ambiguities worth a clarification.
    internal sealed record AiIntentResolution(
        IReadOnlyList<AiIntent> Intents,
        AiIntentEntities Entities,
        AiConstraints Constraints,
        double Confidence,
        bool UsedClassifier,
        IReadOnlyList<string> Ambiguities);

    // Projects the internal plan into the typed public resolution surface.
    private static AiIntentResolution ToResolution(AiIntentPlan plan)
    {
        var constraints = plan.Constraints;
        var exclusions = constraints.ExcludedCategories
            .Concat(constraints.ExcludeTransfers ? ["transfers"] : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fallbackEntities = new AiIntentEntities(
            SearchText: plan.QueryPlan.SearchText,
            CycleHint: plan.QueryPlan.CycleHint,
            Date: null,
            Category: plan.ConversationState.LastCategory,
            LedgerCategory: null,
            Amount: null,
            WishlistItemId: plan.ConversationState.LastWishlistItemId,
            WishlistReference: plan.ConversationState.LastWishlistReference,
            TransactionReference: null,
            TransactionIds: plan.ConversationState.LastMatchedTransactionIds ?? [],
            Exclusions: exclusions);
        var entities = plan.Entities ?? fallbackEntities;
        var ambiguities = new List<string>();
        if (plan.Intents.Count(i => i != AiIntent.General) > 1 && plan.Confidence < 0.8)
        {
            ambiguities.Add("multiple candidate intents");
        }
        return new AiIntentResolution(
            plan.Intents,
            entities,
            constraints,
            plan.Confidence,
            plan.UsedClassifier,
            ambiguities);
    }
}
