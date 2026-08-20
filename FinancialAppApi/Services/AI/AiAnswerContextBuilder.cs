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
                // Action validation only ever needs the ids; the full rows are already
                // emitted as `recentTransactions` below for the intents that need them.
                knownTransactionIds = context.RecentTransactionIds
            }
        };

        var needsLedgerAggregates = has("ledger.activity_count", "ledger.purchase_frequency", "ledger.merchant_search", "ledger.spending_total",
            "ledger.comparison", "ledger.transaction_list", "ledger.anomaly", "ledger.duplicates",
            "category_limits.analysis", "cycle.insights", "allocation.balance", "allocation.performance",
            "ledger.account");
        if (needsLedgerAggregates)
        {
            result["dataScope"] = context.DataScope;
            result["cycleSummaries"] = context.CycleSummaries;
        }
        // derivedMetrics also carries recurring bill status / upcoming bills (recurring intents)
        // and the affordable-wishlist count (wishlist intents), so include it for those too --
        // otherwise those server-computed answers were stripped before the model ever saw them.
        if (needsLedgerAggregates ||
            has("recurring.list", "recurring.upcoming", "wishlist.list", "wishlist.forecast"))
        {
            result["derivedMetrics"] = context.DerivedMetrics;
        }
        if (has("ledger.transaction_list", "ledger.merchant_search", "ledger.anomaly", "ledger.duplicates", "ledger.edit", "ledger.account"))
        {
            result["recentTransactions"] = context.RecentTransactions;
        }
        if (has("ledger.account"))
        {
            result["ledgerAccounts"] = context.LedgerAccounts;
        }
        if (has("recurring.list", "recurring.upcoming", "recurring.add", "recurring.edit"))
        {
            result["recurringPayments"] = context.RecurringPayments;
            if (context.RecurringAdvance != null) result["recurringAdvance"] = context.RecurringAdvance;
            if (context.RecurringReminderStatus != null) result["recurringReminderStatus"] = context.RecurringReminderStatus;
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
        if (has("category_limits.analysis"))
        {
            result["categoryLimits"] = context.CategoryLimits;
        }
        if (has("cycle.insights"))
        {
            result["cycleInsights"] = context.CycleInsights;
        }
        if (has("rewards.summary", "savings_goal.list", "savings_goal.pacing", "savings_goal.scenario",
                "savings_goal.add", "savings_goal.edit"))
        {
            result["rewards"] = context.Rewards;
        }
        if (has("investment.summary", "investment.holding", "investment.allocation"))
        {
            result["investments"] = context.Investments;
        }
        if (has("report.review"))
        {
            result["reportReview"] = context.ReportReview;
        }
        if (has("loan.summary"))
        {
            result["loans"] = context.Loans;
        }
        return result;
    }
}
