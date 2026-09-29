using System.Text.Json.Nodes;
using FinancialAppApi.Database;
using FinancialAppApi.Services.Stability;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.AI.Tools;

// The user's plan: how income is split across buckets, and the stability (emergency) fund's
// position against its target. Reload obligations are counted as obligations still owed, never
// as movements (INVARIANTS: stability reload).
public sealed class GetBudgetPlanTool : IAiTool
{
    private readonly AppDbContext _context;
    private readonly StabilityRecoveryService _stability;

    public GetBudgetPlanTool(AppDbContext context, StabilityRecoveryService stability)
    {
        _context = context;
        _stability = stability;
    }

    public string Name => "get_budget_plan";

    public string Description =>
        "The user's budget plan: the share of income allocated to Essentials, Growth, Stability, and Rewards, and the " +
        "stability (emergency) fund's current balance, target, percent reached, amount still needed, and money taken " +
        "out of it that still has to be put back. Use for 'am I on track for my emergency fund' and to compare actual " +
        "bucket spending (get_cycle_summary ledgerNet) with the plan.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object([]);

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public IReadOnlySet<string> AmountProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "currentBalance", "targetStabilityFund", "percentReached", "stillNeeded", "owedBackFromDrawdowns"
    };

    public string ProgressLabel(AiToolArgs args) => "Checking your budget plan";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var setting = await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (setting == null)
            return AiToolResult.Of(new { configured = false, note = "The user has not set up a budget plan yet." });

        // The plan is withheld as a whole in sensitive mode, as the assistant always has.
        if (context.SensitiveMode)
            return AiToolResult.Of(new { configured = true, note = "The budget plan is hidden while sensitive mode is on." });

        var allocation = new
        {
            essentials = setting.EssentialsAlloc,
            growth = setting.GrowthAlloc,
            stability = setting.StabilityAlloc,
            rewards = setting.RewardsAlloc,
            note = "Fractions of income allocated to each bucket."
        };

        var state = await _stability.GetStabilityStateAsync(setting, context.Today, cancellationToken: cancellationToken);
        return AiToolResult.Of(new
        {
            configured = true,
            allocation,
            stabilityFund = new
            {
                currentBalance = state.CurrentBalance,
                targetStabilityFund = state.Target,
                percentReached = state.Target > 0 ? Math.Round(state.CurrentBalance / state.Target * 100m, 1) : (decimal?)null,
                stillNeeded = state.Target > 0 ? Math.Max(0m, state.Target - state.CurrentBalance) : (decimal?)null,
                owedBackFromDrawdowns = state.OutstandingObligation,
                note = state.Target > 0 ? null : "No stability fund target is set."
            },
            overflowRedirect = string.IsNullOrWhiteSpace(setting.StabilityOverflowRedirect) ? null : setting.StabilityOverflowRedirect
        });
    }
}
