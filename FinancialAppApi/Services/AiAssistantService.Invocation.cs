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
        normalized = new AiInvocationContext(surface, preset, cycleKey, range, context.SavingsGoalId, loanId);
        return true;
    }

    private static AiConversationState? ApplyInvocationState(
        AiConversationState? state,
        AiInvocationContext? context)
    {
        if (context == null) return state;
        return (state ?? new AiConversationState()) with
        {
            LastInvestmentRange = context.InvestmentRange ?? state?.LastInvestmentRange,
            LastSavingsGoalId = context.SavingsGoalId ?? state?.LastSavingsGoalId,
            LastReportCycleKey = context.CycleKey ?? state?.LastReportCycleKey,
            LastLoanId = context.LoanId ?? state?.LastLoanId
        };
    }

}
