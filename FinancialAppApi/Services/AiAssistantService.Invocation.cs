using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private static readonly HashSet<string> InvocationSurfaces =
        new(StringComparer.OrdinalIgnoreCase) { "dashboard", "reports", "recurring", "ledger", "wishlist", "drafts", "settings", "investments", "documents" };

    private static readonly HashSet<string> InvocationPresets =
        new(StringComparer.OrdinalIgnoreCase) { "report-review", "investment-explain", "rewards-plan", "loan-explain" };

    private static readonly HashSet<string> InvocationRanges =
        new(StringComparer.OrdinalIgnoreCase) { "1m", "3m", "6m", "1y", "3y", "5y", "all" };

    private static bool TryNormalizeInvocationContext(
        AiInvocationContext? context,
        out AiInvocationContext? normalized,
        out string? error)
    {
        normalized = null;
        error = null;
        if (context == null) return true;
        var surface = context.Surface?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(surface) || !InvocationSurfaces.Contains(surface))
        {
            error = "That screen is not a valid Ask AI context.";
            return false;
        }
        var preset = string.IsNullOrWhiteSpace(context.Preset) ? null : context.Preset.Trim().ToLowerInvariant();
        if (preset != null && !InvocationPresets.Contains(preset))
        {
            error = "That Ask AI explanation type is not available.";
            return false;
        }
        var range = string.IsNullOrWhiteSpace(context.InvestmentRange) ? null : context.InvestmentRange.Trim().ToLowerInvariant();
        if (range != null && !InvocationRanges.Contains(range))
        {
            error = "That investment time range is not available.";
            return false;
        }
        var cycleKey = string.IsNullOrWhiteSpace(context.CycleKey) ? null : context.CycleKey.Trim();
        if (cycleKey != null && !Regex.IsMatch(cycleKey, @"^(?:19|20)\d{2}-(?:0[1-9]|1[0-2])$"))
        {
            error = "That cycle reference is not valid.";
            return false;
        }
        if (context.SavingsGoalId is <= 0)
        {
            error = "That savings goal reference is not valid.";
            return false;
        }
        var loanId = string.IsNullOrWhiteSpace(context.LoanId) ? null : context.LoanId.Trim();
        if (loanId is { Length: > 100 })
        {
            error = "That loan reference is not valid.";
            return false;
        }
        normalized = new AiInvocationContext(surface, preset, cycleKey, range, context.SavingsGoalId, context.HasPendingLocalChanges, loanId);
        return true;
    }

    private static AiConversationState? ApplyInvocationState(
        AiConversationState? state,
        AiInvocationContext? context)
    {
        if (context == null) return state;
        return (state ?? new AiConversationState(null, null, null, null)) with
        {
            LastInvestmentRange = context.InvestmentRange ?? state?.LastInvestmentRange,
            LastSavingsGoalId = context.SavingsGoalId ?? state?.LastSavingsGoalId,
            LastReportCycleKey = context.CycleKey ?? state?.LastReportCycleKey,
            LastInvestmentTopic = context.Preset == "investment-explain" ? "portfolio" : state?.LastInvestmentTopic,
            LastRewardsTopic = context.Preset == "rewards-plan" ? "plan" : state?.LastRewardsTopic,
            LastLoanId = context.LoanId ?? state?.LastLoanId
        };
    }

    private static string ApplyInvocationMessage(string message, AiInvocationContext? context)
    {
        if (context == null) return message;
        var suffix = new List<string>();
        if (context.CycleKey != null) suffix.Add($"for cycle {context.CycleKey}");
        if (context.InvestmentRange != null) suffix.Add($"using investment range {context.InvestmentRange}");
        if (context.Preset == "report-review") suffix.Add("report review");
        if (context.Preset == "investment-explain") suffix.Add("portfolio explanation");
        if (context.Preset == "rewards-plan") suffix.Add("Rewards plan");
        if (context.Preset == "loan-explain") suffix.Add("loan summary");
        return suffix.Count == 0 ? message : $"{message} ({string.Join(", ", suffix)})";
    }

    private static AiIntentPlan ApplyInvocationPresetPlan(AiIntentPlan plan, AiInvocationContext? context)
    {
        if (context?.Preset == "loan-explain")
        {
            var loanIntents = plan.Intents.Append(AiIntent.LoanSummary).Distinct().ToList();
            return plan with
            {
                Intents = loanIntents,
                QueryPlan = BuildQueryPlan(
                    loanIntents,
                    needsTransactionDetail: false,
                    needsCycleSummary: false,
                    needsCycleComparison: false,
                    needsWishlist: false,
                    needsWishlistForecast: false,
                    needsRecurring: false,
                    needsBudgetTargets: false,
                    needsCategoryLimits: false,
                    needsCycleInsights: false,
                    searchText: null,
                    cycleHint: null,
                    queryText: plan.QueryPlan.QueryText,
                    needsLoans: true)
            };
        }
        if (context?.Preset != "rewards-plan") return plan;

        var intents = plan.Intents
            .Concat([AiIntent.RewardsSummary, AiIntent.SavingsGoalPacing, AiIntent.WishlistForecast])
            .Distinct()
            .ToList();
        var queryPlan = BuildQueryPlan(
            intents,
            needsTransactionDetail: false,
            needsCycleSummary: true,
            needsCycleComparison: false,
            needsWishlist: true,
            needsWishlistForecast: true,
            needsRecurring: false,
            needsBudgetTargets: true,
            needsCategoryLimits: false,
            needsCycleInsights: false,
            searchText: null,
            cycleHint: plan.QueryPlan.CycleHint,
            queryText: plan.QueryPlan.QueryText,
            transactionIds: plan.QueryPlan.TransactionIds,
            wishlistItemId: plan.QueryPlan.WishlistItemId);
        return plan with { Intents = intents, QueryPlan = queryPlan };
    }

    // Keep optional payload groups intent-specific so unrelated controls do not distract the model
    // or inflate the structured output contract.
    private static readonly HashSet<AiIntent> LedgerFilterIntents =
    [
        AiIntent.Navigation, AiIntent.LedgerTransactionList, AiIntent.LedgerMerchantSearch,
        AiIntent.LedgerActivityCount, AiIntent.LedgerSpendingTotal
    ];

    private static bool WantsLedgerFilterControls(AiIntentPlan plan) =>
        plan.Intents.Any(LedgerFilterIntents.Contains);

    private static bool WantsReminderControls(AiIntentPlan plan) =>
        // Read-only recurring questions (cost, list, upcoming bills) do not need reminder-edit
        // fields, so do not carry that optional group on read-only requests.
        plan.Intents.Contains(AiIntent.RecurringEdit);
}
