namespace FinancialAppApi.Services;

// Builds the small, intent-specific context shown to the answer model. The full AiContext is
// retained internally for action validation, but unrelated datasets are deliberately omitted
// from the prompt so the model cannot use stale or irrelevant financial blocks.
public partial class AiAssistantService
{
    private static object BuildAnswerContext(AiContext context)
    {
        var intents = context.IntentNames;
        var has = (params string[] names) => names.Any(name => intents.Contains(name, StringComparer.OrdinalIgnoreCase));
        var result = new Dictionary<string, object?>
        {
            ["currency"] = context.Currency,
            ["today"] = context.Today,
            ["sensitiveMode"] = context.SensitiveMode,
            ["activeCycle"] = context.ActiveCycle,
            ["requestedCycles"] = context.RequestedCycles,
            ["intents"] = intents,
            ["queryPlan"] = context.QueryPlan,
            ["sufficiency"] = context.Sufficiency,
            ["constraints"] = context.Constraints,
            ["conversationState"] = context.ConversationState,
            ["actionContext"] = new
            {
                categories = context.Categories,
                ledgerCategories = context.LedgerCategories,
                knownTransactionIds = context.RecentTransactions
            }
        };

        if (has("ledger.activity_count", "ledger.merchant_search", "ledger.spending_total", "ledger.comparison",
                "ledger.transaction_list", "ledger.anomaly", "ledger.duplicates", "allocation.balance", "allocation.performance"))
        {
            result["dataScope"] = context.DataScope;
            result["cycleSummaries"] = context.CycleSummaries;
            result["derivedMetrics"] = context.DerivedMetrics;
        }
        if (has("ledger.transaction_list", "ledger.merchant_search", "ledger.anomaly", "ledger.duplicates", "ledger.edit"))
        {
            result["recentTransactions"] = context.RecentTransactions;
        }
        if (has("recurring.list", "recurring.upcoming", "recurring.add", "recurring.edit"))
        {
            result["recurringPayments"] = context.RecurringPayments;
        }
        if (has("wishlist.list", "wishlist.forecast", "wishlist.add", "wishlist.edit"))
        {
            result["wishlistItems"] = context.WishlistItems;
            result["wishlistForecast"] = context.WishlistForecast;
        }
        if (has("allocation.balance", "allocation.performance"))
        {
            result["budgetTargets"] = context.BudgetTargets;
        }
        return result;
    }
}
