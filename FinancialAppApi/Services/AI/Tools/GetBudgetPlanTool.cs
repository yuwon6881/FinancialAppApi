using System.Text.Json.Nodes;
using FinancialAppApi.Database;
using FinancialAppApi.Services.Stability;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.AI.Tools;

// The user's plan: how income is split across buckets, and the stability (emergency) fund's
// position against its target. Reload obligations are counted as obligations still owed, never
// as movements (INVARIANTS: stability reload). The repayment schedule comes from the recovery
// planner itself: given only the total owed, the model assumed it was all due next cycle.
public sealed class GetBudgetPlanTool : IAiTool
{
    private const string ReloadRule =
        "Money taken out of Stability is put back over the three cycles after the cycle it left in; the cycle it " +
        "left in asks for nothing. Each withdrawal cycle has its own three-cycle window, and when windows overlap " +
        "their shares add up to one ask. An ordinary salary share into Stability does not count as putting money " +
        "back, and reaching the target clears everything owed. A withdrawal marked as not needing to be put back " +
        "creates nothing to repay.";

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
        "stability (emergency) fund's balance, target, and money taken out of it that still has to be put back -- with " +
        "what is due this cycle and the cycle-by-cycle repayment schedule. Pass extraWithdrawal for 'what if I take " +
        "another X out of my emergency fund': the result gives the new schedule. Use for emergency fund questions and to " +
        "compare actual bucket spending (get_cycle_summary ledgerNet) with the plan. Quote its figures; never work out a " +
        "repayment yourself.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("extraWithdrawal", AiToolSchema.Number("Hypothetical amount taken out of Stability today, for a what-if. Omit otherwise.", 0))
    ]);

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public IReadOnlySet<string> AmountProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "currentBalance", "targetStabilityFund", "percentReached", "stillNeeded", "owedBackFromDrawdowns",
        "dueThisCycle", "putBackSoFar", "stillToPutBack", "stillOwed", "putBack", "stillOwedAfter",
        "extraWithdrawal", "fundBalanceAfter", "changeFromCurrentPlan"
    };

    public string ProgressLabel(AiToolArgs args) => "Checking your budget plan";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var extraWithdrawal = args.OptionalDecimal("extraWithdrawal", 0m, 1_000_000_000m);
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

        var schedule = await _stability.GetRecoveryScheduleAsync(setting, context.Today, cancellationToken);
        return AiToolResult.Of(new
        {
            configured = true,
            allocation,
            stabilityFund = new
            {
                currentBalance = schedule.CurrentBalance,
                targetStabilityFund = schedule.Target,
                percentReached = schedule.Target > 0 ? Math.Round(schedule.CurrentBalance / schedule.Target * 100m, 1) : (decimal?)null,
                stillNeeded = schedule.Target > 0 ? Math.Max(0m, schedule.Target - schedule.CurrentBalance) : (decimal?)null,
                owedBackFromDrawdowns = schedule.Outstanding,
                note = schedule.Target > 0 ? null : "No stability fund target is set."
            },
            reloadPlan = schedule.Outstanding > 0m || extraWithdrawal > 0m ? DescribePlan(schedule, context.CycleDay) : null,
            whatIf = extraWithdrawal > 0m ? DescribeWhatIf(schedule, extraWithdrawal.Value, context) : null,
            overflowRedirect = string.IsNullOrWhiteSpace(setting.StabilityOverflowRedirect) ? null : setting.StabilityOverflowRedirect
        });
    }

    private static object DescribePlan(StabilityRecoverySchedule schedule, int cycleDay)
    {
        var aggregate = schedule.Plan.Aggregate;
        return new
        {
            rule = ReloadRule,
            currentCycle = new
            {
                cycle = Cycle(schedule.CycleKey, cycleDay),
                dueThisCycle = aggregate.RequiredThisCycle,
                putBackSoFar = aggregate.ToppedUpThisCycle,
                stillToPutBack = aggregate.OutstandingThisCycle,
                nothingDueYet = aggregate.IsDeferred,
                overdue = aggregate.IsOverdue
            },
            byWithdrawalCycle = schedule.Plan.Cohorts
                .Where(cohort => cohort.RemainingShortfall > 0m)
                .Select(cohort => new
                {
                    tookOutInCycle = cohort.OriginCycleKey,
                    firstWithdrawalDate = cohort.FromDate.ToString("yyyy-MM-dd"),
                    stillOwed = cohort.RemainingShortfall,
                    dueThisCycle = cohort.RequiredThisCycle,
                    cyclesLeftIncludingThis = cohort.IsDeferred ? (int?)null : cohort.CyclesRemaining,
                    startsNextCycle = cohort.IsDeferred,
                    overdue = cohort.IsOverdue
                })
                .ToList(),
            upcoming = Upcoming(Project(schedule, []), cycleDay, null),
            upcomingAssumes = "This cycle's remaining ask and every later ask are put back in full, and nothing else is taken out or reaches the target."
        };
    }

    private static object DescribeWhatIf(StabilityRecoverySchedule schedule, decimal amount, AiToolContext context)
    {
        var balanceAfter = schedule.CurrentBalance - amount;
        // Replay clears the whole queue whenever the balance is at or above the target, so a
        // withdrawal that leaves the fund there owes nothing.
        var createsObligation = !(schedule.Target > 0m && balanceAfter >= schedule.Target);
        var current = Project(schedule, []);
        var extra = createsObligation
            ? new[] { new RecoveryCohortInput(schedule.CycleKey, context.Today, 1, amount, 0m) }
            : [];
        var withExtra = Project(schedule, extra);
        return new
        {
            extraWithdrawal = amount,
            takenInCycle = schedule.CycleKey,
            fundBalanceAfter = balanceAfter,
            exceedsFundBalance = amount > schedule.CurrentBalance,
            createsObligation,
            reason = createsObligation
                ? "Nothing extra is due this cycle; the amount is spread over the three cycles after this one, on top of the current plan."
                : "The fund would still be at or above its target, so nothing has to be put back.",
            dueThisCycle = schedule.Plan.Aggregate.RequiredThisCycle,
            upcoming = Upcoming(withExtra, context.CycleDay, current)
        };
    }

    private static IReadOnlyList<RecoveryProjectionCycle> Project(
        StabilityRecoverySchedule schedule,
        IReadOnlyList<RecoveryCohortInput> extra)
    {
        // Start from the next cycle, after this cycle's remaining ask. A new withdrawal joins the
        // back of the queue, so it never absorbs that ask.
        var afterThisCycle = StabilityRecoveryProjection.Discharge(
            schedule.Cohorts, schedule.Plan.Aggregate.OutstandingThisCycle).Concat(extra);
        return StabilityRecoveryProjection.Project(
            afterThisCycle,
            StabilityRecoveryProjection.NextCycleKey(schedule.CycleKey),
            FinancialConstants.StabilityRecoveryCycles,
            FinancialConstants.StabilityRecoveryCycles + 1);
    }

    private static List<object> Upcoming(
        IReadOnlyList<RecoveryProjectionCycle> cycles,
        int cycleDay,
        IReadOnlyList<RecoveryProjectionCycle>? baseline) =>
        cycles.Select(cycle => (object)new
        {
            cycle = Cycle(cycle.CycleKey, cycleDay),
            putBack = cycle.PutBack,
            stillOwedAfter = cycle.StillOwedAfter,
            changeFromCurrentPlan = baseline == null
                ? (decimal?)null
                : cycle.PutBack - (baseline.FirstOrDefault(item => item.CycleKey == cycle.CycleKey)?.PutBack ?? 0m)
        }).ToList();

    private static object Cycle(string key, int cycleDay)
    {
        var cycle = new AiCycle(int.Parse(key[..4]), int.Parse(key[5..]));
        var range = AiCycleResolver.Range(cycle, cycleDay);
        return new { key, from = range.FirstDate.ToString("yyyy-MM-dd"), to = range.LastDate.ToString("yyyy-MM-dd") };
    }
}
