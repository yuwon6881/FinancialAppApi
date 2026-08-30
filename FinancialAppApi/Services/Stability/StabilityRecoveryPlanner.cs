namespace FinancialAppApi.Services.Stability;

/// <summary>
/// What this cycle asks for, before any particular income amount is known.
/// <para>
/// <c>CyclesRemaining</c> counts this cycle and never falls below 1: a window that has run out
/// still owes its remainder, exactly as an overdue savings goal does — <c>IsOverdue</c> is what
/// tells those two apart, so the card can stop calling every cycle "the last one".
/// <c>ToppedUpThisCycle</c> is
/// Extra Stability reimbursement received this cycle. New salaries persist it explicitly; only
/// current-cycle legacy null rows use the narrow generated-split inference fallback.
/// </para>
/// </summary>
public sealed record RecoveryPace(
    decimal Shortfall,
    int CyclesRemaining,
    decimal RequiredThisCycle,
    decimal ToppedUpThisCycle,
    decimal OutstandingThisCycle,
    bool IsOverdue);

public sealed record RecoveryCohortInput(
    string OriginCycleKey,
    DateOnly FromDate,
    int TransactionCount,
    decimal RemainingShortfall,
    decimal RepaidThisCycle);

public sealed record RecoveryCohortPace(
    string OriginCycleKey,
    DateOnly FromDate,
    int TransactionCount,
    decimal RemainingShortfall,
    int CyclesRemaining,
    decimal RequiredThisCycle,
    bool IsOverdue);

public sealed record RecoveryCohortPlan(
    RecoveryPace Aggregate,
    IReadOnlyList<RecoveryCohortPace> Cohorts);

/// <summary>
/// Pure emergency-fund recovery math: how much of an explicit reload obligation to ask back each
/// cycle, and how much of a given salary can safely provide it.
/// Kept free of EF and clocks -- like <see cref="SavingsGoals.SavingsGoalPacing"/>, which it
/// deliberately echoes -- and mirrored on the client in <c>lib/stabilityRecovery.ts</c>.
/// </summary>
public static class StabilityRecoveryPlanner
{
    /// <summary>
    /// Cycles left in the recovery window, counting the current one. Goes to zero or below once the
    /// window has elapsed — like <c>SavingsGoalPacing.CyclesRemaining</c>, and for the same reason:
    /// clamping here would make an overdue plan indistinguishable from its final cycle, and the card
    /// would announce "the last cycle of the plan" every cycle from then on. <c>ComputePace</c> does
    /// the clamping it needs internally.
    /// </summary>
    public static int CyclesRemaining(string? lastDrawdownCycleKey, string currentCycleKey, int horizon)
    {
        if (horizon < 1) horizon = 1;
        if (!TryParseCycleKey(lastDrawdownCycleKey, out var fromYear, out var fromMonth) ||
            !TryParseCycleKey(currentCycleKey, out var toYear, out var toMonth))
        {
            return horizon;
        }

        var elapsed = (toYear - fromYear) * 12 + (toMonth - fromMonth);
        return horizon - Math.Max(0, elapsed);
    }

    public static RecoveryPace ComputePace(
        decimal outstandingShortfall,
        int cyclesRemaining,
        decimal toppedUpThisCycle)
    {
        var funded = Math.Max(0m, toppedUpThisCycle);
        var cycles = Math.Max(1, cyclesRemaining);

        // Anchored on the shortfall BEFORE this cycle's repayments, not on the cycle's opening
        // balance. The opening balance was the obvious anchor (SavingsGoalPacing uses it) and it was
        // wrong here: a savings goal's target exists in advance, but a drawdown can happen *during*
        // the cycle, and then the cycle opened with no shortfall at all -- so the plan asked for
        // nothing in the very cycle the money was spent, and the card's "outstanding this cycle"
        // gate hid it exactly when it was needed.
        //
        // Shortfall + funded holds just as still while money goes in, which is the property the
        // opening balance was chosen for: funding moves one down and the other up by the same
        // amount, so the requirement does not shrink as it is met.
        var shortfall = Math.Max(0m, outstandingShortfall);
        var anchor = shortfall + funded;
        var required = cycles <= 1 ? anchor : RoundUpToCent(anchor / cycles);

        // Capped by the live shortfall too, so the last stretch only asks for what is actually left.
        var outstanding = Math.Clamp(required - funded, 0m, shortfall);

        return new RecoveryPace(
            shortfall, cycles, required, funded, outstanding, cyclesRemaining <= 0);
    }

    /// <summary>
    /// Gives each origin cycle its own recovery window while retaining one combined amount to ask
    /// from income. A cohort's anchor is its live remainder plus money applied to that cohort during
    /// this cycle, so its quoted share does not shrink while the user funds it.
    /// </summary>
    public static RecoveryCohortPlan ComputeCohortPlan(
        IEnumerable<RecoveryCohortInput> cohorts,
        string currentCycleKey,
        int horizon,
        decimal outstandingShortfall,
        decimal toppedUpThisCycle)
    {
        var paces = cohorts
            .Where(cohort => cohort.RemainingShortfall > 0m || cohort.RepaidThisCycle > 0m)
            .GroupBy(cohort => cohort.OriginCycleKey, StringComparer.Ordinal)
            .Select(group => new RecoveryCohortInput(
                group.Key,
                group.Min(cohort => cohort.FromDate),
                group.Sum(cohort => Math.Max(1, cohort.TransactionCount)),
                group.Sum(cohort => Math.Max(0m, cohort.RemainingShortfall)),
                group.Sum(cohort => Math.Max(0m, cohort.RepaidThisCycle))))
            .Select(cohort =>
            {
                var rawCyclesRemaining = CyclesRemaining(
                    cohort.OriginCycleKey,
                    currentCycleKey,
                    horizon);
                var cyclesRemaining = Math.Max(1, rawCyclesRemaining);
                var anchor = Math.Max(0m, cohort.RemainingShortfall)
                    + Math.Max(0m, cohort.RepaidThisCycle);
                var required = cyclesRemaining <= 1
                    ? anchor
                    : RoundUpToCent(anchor / cyclesRemaining);
                return new RecoveryCohortPace(
                    cohort.OriginCycleKey,
                    cohort.FromDate,
                    Math.Max(1, cohort.TransactionCount),
                    Math.Max(0m, cohort.RemainingShortfall),
                    cyclesRemaining,
                    required,
                    rawCyclesRemaining <= 0 && cohort.RemainingShortfall > 0m);
            })
            .OrderBy(cohort => cohort.OriginCycleKey, StringComparer.Ordinal)
            .ThenBy(cohort => cohort.FromDate)
            .ToList();

        var shortfall = Math.Max(0m, outstandingShortfall);
        var funded = Math.Max(0m, toppedUpThisCycle);
        var requiredThisCycle = paces.Sum(cohort => cohort.RequiredThisCycle);
        var outstandingThisCycle = Math.Clamp(requiredThisCycle - funded, 0m, shortfall);
        var open = paces.Where(cohort => cohort.RemainingShortfall > 0m).ToList();
        var urgentCyclesRemaining = open.Count == 0
            ? Math.Max(1, horizon)
            : open.Min(cohort => cohort.CyclesRemaining);
        var isOverdue = open.Any(cohort => cohort.IsOverdue);

        return new RecoveryCohortPlan(
            new RecoveryPace(
                shortfall,
                urgentCyclesRemaining,
                requiredThisCycle,
                funded,
                outstandingThisCycle,
                isOverdue),
            paces);
    }

    public static string CycleKey(int year, int monthIndex) => $"{year:0000}-{monthIndex:00}";

    private static bool TryParseCycleKey(string? key, out int year, out int monthIndex)
    {
        year = 0;
        monthIndex = 0;
        if (string.IsNullOrWhiteSpace(key)) return false;

        var parts = key.Split('-');
        return parts.Length == 2
            && int.TryParse(parts[0], out year)
            && int.TryParse(parts[1], out monthIndex)
            && monthIndex is >= 1 and <= 12;
    }

    private static decimal RoundUpToCent(decimal value) => Math.Ceiling(value * 100m) / 100m;
}
