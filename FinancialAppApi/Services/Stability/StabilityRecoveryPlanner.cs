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
/// <para>
/// <c>IsDeferred</c> means every plan that still owes money opens in a later cycle, so this cycle
/// asks for nothing. It is not the same as being ahead of the plan: nothing was funded, nothing was
/// due. A funded cycle also reaches <c>OutstandingThisCycle == 0</c>, which is why this is its own
/// flag rather than an inference from the amounts.
/// </para>
/// </summary>
public sealed record RecoveryPace(
    decimal Shortfall,
    int CyclesRemaining,
    decimal RequiredThisCycle,
    decimal ToppedUpThisCycle,
    decimal OutstandingThisCycle,
    bool IsOverdue,
    bool IsDeferred = false);

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
    bool IsOverdue,
    bool IsDeferred = false);

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
    /// How long the spending cycle itself waits before its repayment plan opens. One cycle: the
    /// money leaves the fund partway through a cycle whose income has already been split and largely
    /// spent, so asking for a share of it back in that same cycle asks for money the buckets no
    /// longer hold — right after the app has said the fund exists for exactly this. The plan runs
    /// over the <c>StabilityRecoveryCycles</c> cycles that follow; the spending cycle carries only
    /// the reminder.
    /// </summary>
    public const int GraceCycles = 1;

    /// <summary>
    /// Cycles left in the recovery window, counting the current one. Goes to zero or below once the
    /// window has elapsed — like <c>SavingsGoalPacing.CyclesRemaining</c>, and for the same reason:
    /// clamping here would make an overdue plan indistinguishable from its final cycle, and the card
    /// would announce "the last cycle of the plan" every cycle from then on. <c>ComputePace</c> does
    /// the clamping it needs internally.
    /// <para>
    /// The spending cycle is not one of them: the full horizon stands through both the spending cycle
    /// and the first repayment cycle, and only then counts down. See <see cref="GraceCycles"/>.
    /// </para>
    /// </summary>
    public static int CyclesRemaining(string? originCycleKey, string currentCycleKey, int horizon)
    {
        if (horizon < 1) horizon = 1;
        if (!TryElapsedCycles(originCycleKey, currentCycleKey, out var elapsed)) return horizon;

        return horizon - Math.Max(0, elapsed - GraceCycles);
    }

    /// <summary>
    /// Whether the plan for <paramref name="originCycleKey"/> has not opened yet, so nothing is due
    /// against it this cycle. True in the spending cycle itself, and ahead of it for a forward-dated
    /// withdrawal. An unknown origin cycle is never deferred: the fallback path has no cycle to defer
    /// to, and answering "nothing is due" there would quietly drop a real obligation.
    /// </summary>
    public static bool IsDeferred(string? originCycleKey, string currentCycleKey)
        => TryElapsedCycles(originCycleKey, currentCycleKey, out var elapsed) && elapsed < GraceCycles;

    private static bool TryElapsedCycles(string? originCycleKey, string currentCycleKey, out int elapsed)
    {
        elapsed = 0;
        if (!TryParseCycleKey(originCycleKey, out var fromYear, out var fromMonth) ||
            !TryParseCycleKey(currentCycleKey, out var toYear, out var toMonth))
        {
            return false;
        }

        elapsed = (toYear - fromYear) * 12 + (toMonth - fromMonth);
        return true;
    }

    public static RecoveryPace ComputePace(
        decimal outstandingShortfall,
        int cyclesRemaining,
        decimal toppedUpThisCycle,
        bool isDeferred = false)
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
        // A deferred plan asks for nothing at all rather than a share of it. The horizon it will be
        // spread over is unchanged; it simply has not opened. Money put back anyway still counts --
        // it lands in ToppedUpThisCycle and shrinks the shortfall the first real cycle divides.
        var required = isDeferred
            ? 0m
            : cycles <= 1 ? anchor : RoundUpToCent(anchor / cycles);

        // Capped by the live shortfall too, so the last stretch only asks for what is actually left.
        var outstanding = Math.Clamp(required - funded, 0m, shortfall);

        return new RecoveryPace(
            shortfall, cycles, required, funded, outstanding, cyclesRemaining <= 0, isDeferred);
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
                var isDeferred = IsDeferred(cohort.OriginCycleKey, currentCycleKey);
                var anchor = Math.Max(0m, cohort.RemainingShortfall)
                    + Math.Max(0m, cohort.RepaidThisCycle);
                var required = isDeferred
                    ? 0m
                    : cyclesRemaining <= 1
                        ? anchor
                        : RoundUpToCent(anchor / cyclesRemaining);
                return new RecoveryCohortPace(
                    cohort.OriginCycleKey,
                    cohort.FromDate,
                    Math.Max(1, cohort.TransactionCount),
                    Math.Max(0m, cohort.RemainingShortfall),
                    cyclesRemaining,
                    required,
                    rawCyclesRemaining <= 0 && cohort.RemainingShortfall > 0m,
                    isDeferred);
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
        // Only when *every* plan that still owes money starts later. One older cohort still due keeps
        // a combined ask, and the card should talk about that ask rather than about the newest
        // withdrawal's grace cycle.
        var isDeferred = open.Count > 0 && open.All(cohort => cohort.IsDeferred);

        return new RecoveryCohortPlan(
            new RecoveryPace(
                shortfall,
                urgentCyclesRemaining,
                requiredThisCycle,
                funded,
                outstandingThisCycle,
                isOverdue,
                isDeferred),
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
