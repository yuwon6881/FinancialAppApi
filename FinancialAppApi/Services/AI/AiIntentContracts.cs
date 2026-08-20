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
        LedgerPurchaseFrequency,
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
        CategoryLimits,
        CycleInsights,
        AllocationBalance,
        AllocationPerformance,
        RewardsSummary,
        SavingsGoalList,
        SavingsGoalPacing,
        SavingsGoalScenario,
        SavingsGoalAdd,
        SavingsGoalEdit,
        InvestmentSummary,
        InvestmentHolding,
        InvestmentAllocation,
        LedgerAccount,
        LoanSummary,
        ReportReview
    }

    internal sealed record AiCapabilityDefinition(
        AiIntent Intent,
        string Name,
        string? Topic,
        bool RequiresSensitiveReveal = false);

    internal static readonly IReadOnlyList<AiCapabilityDefinition> AiCapabilities =
    [
        new(AiIntent.General, "general", null),
        new(AiIntent.Navigation, "navigation", null),
        new(AiIntent.LedgerActivityCount, "ledger.activity_count", "transactional"),
        new(AiIntent.LedgerPurchaseFrequency, "ledger.purchase_frequency", "transactional"),
        new(AiIntent.LedgerMerchantSearch, "ledger.merchant_search", "transactional"),
        new(AiIntent.LedgerSpendingTotal, "ledger.spending_total", "transactional", true),
        new(AiIntent.LedgerTransactionList, "ledger.transaction_list", "transactional"),
        new(AiIntent.LedgerComparison, "ledger.comparison", "transactional", true),
        new(AiIntent.LedgerAdd, "ledger.add", "transactional"),
        new(AiIntent.LedgerEdit, "ledger.edit", "transactional"),
        new(AiIntent.LedgerAnomaly, "ledger.anomaly", "transactional", true),
        new(AiIntent.LedgerDuplicates, "ledger.duplicates", "transactional", true),
        new(AiIntent.WishlistList, "wishlist.list", "wishlist"),
        new(AiIntent.WishlistForecast, "wishlist.forecast", "wishlist", true),
        new(AiIntent.WishlistAdd, "wishlist.add", "wishlist"),
        new(AiIntent.WishlistEdit, "wishlist.edit", "wishlist"),
        new(AiIntent.RecurringList, "recurring.list", "recurring"),
        new(AiIntent.RecurringUpcoming, "recurring.upcoming", "recurring"),
        new(AiIntent.RecurringAdd, "recurring.add", "recurring"),
        new(AiIntent.RecurringEdit, "recurring.edit", "recurring"),
        new(AiIntent.CategoryLimits, "category_limits.analysis", "transactional", true),
        new(AiIntent.CycleInsights, "cycle.insights", "transactional", true),
        new(AiIntent.AllocationBalance, "allocation.balance", "transactional", true),
        new(AiIntent.AllocationPerformance, "allocation.performance", "transactional", true),
        new(AiIntent.RewardsSummary, "rewards.summary", "rewards", true),
        new(AiIntent.SavingsGoalList, "savings_goal.list", "rewards", true),
        new(AiIntent.SavingsGoalPacing, "savings_goal.pacing", "rewards", true),
        new(AiIntent.SavingsGoalScenario, "savings_goal.scenario", "rewards", true),
        new(AiIntent.SavingsGoalAdd, "savings_goal.add", "rewards", true),
        new(AiIntent.SavingsGoalEdit, "savings_goal.edit", "rewards", true),
        new(AiIntent.InvestmentSummary, "investment.summary", "investment", true),
        new(AiIntent.InvestmentHolding, "investment.holding", "investment", true),
        new(AiIntent.InvestmentAllocation, "investment.allocation", "investment", true),
        new(AiIntent.LedgerAccount, "ledger.account", "transactional", true),
        new(AiIntent.LoanSummary, "loan.summary", "loan", true),
        new(AiIntent.ReportReview, "report.review", "report", true)
    ];

    private static readonly Dictionary<string, AiIntent> IntentByName = AiCapabilities
        .ToDictionary(capability => capability.Name, capability => capability.Intent, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<AiIntent, string> NameByIntent =
        IntentByName.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    internal static IReadOnlyList<string> KnownIntentNames => AiCapabilities.Select(capability => capability.Name).ToList();

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
        IReadOnlyList<string> Exclusions,
        string? LedgerAccountReference = null);

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
