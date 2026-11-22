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
    bool IsDeferred = false,
    // The part of RequiredThisCycle that is earlier installments not yet put back.
    decimal CarriedThisCycle = 0m);

public sealed record RecoveryCohortInput(
    string OriginCycleKey,
    DateOnly FromDate,
    int TransactionCount,
    decimal RemainingShortfall,
    decimal RepaidThisCycle,
    int? RepaymentCycles = null,
    // What the withdrawal was before anything went back; the installment schedule is fixed against
    // it. Null only for a withdrawal nothing has been put back against yet.
    decimal? OriginalAmount = null);

public sealed record RecoveryCohortPace(
    string OriginCycleKey,
    DateOnly FromDate,
    int TransactionCount,
    decimal RemainingShortfall,
    int CyclesRemaining,
    decimal RequiredThisCycle,
    bool IsOverdue,
    bool IsDeferred = false,
    int? RepaymentCycles = null);

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
    /// Prices every open withdrawal against its own fixed cumulative installments and returns one
    /// <em>pooled</em> ask: what has fallen due across all plans, less everything already put back
    /// against any of them.
    /// <para>
    /// Ledger completion is FIFO, so a payment often lands on an older plan than the one that needed
    /// it. Judging each plan alone stranded that surplus and left a newer plan overdue while its owner
    /// paid every ask in full. Money is fungible across plans here; the per-plan figures are the
    /// pooled ask allocated oldest first, so they still add up to the aggregate.
    /// </para>
    /// <para>
    /// <c>CarriedThisCycle</c> is the part of the ask that is earlier installments rather than this
    /// cycle's own. Withdrawals with the same origin cycle and term are reported as one cohort.
    /// </para>
    /// </summary>
    public static RecoveryCohortPlan ComputeCohortPlan(
        IEnumerable<RecoveryCohortInput> cohorts,
        string currentCycleKey,
        int horizon,
        decimal outstandingShortfall,
        decimal toppedUpThisCycle)
    {
        var groups = cohorts
            .Where(cohort => cohort.RemainingShortfall > 0m || cohort.RepaidThisCycle > 0m
                || CyclesRemaining(cohort.OriginCycleKey, currentCycleKey, cohort.RepaymentCycles ?? horizon) > 0)
            .GroupBy(cohort => (cohort.OriginCycleKey, RepaymentCycles: cohort.RepaymentCycles ?? horizon))
            .Select(group =>
            {
                var (originCycleKey, term) = group.Key;
                var rawCyclesRemaining = CyclesRemaining(originCycleKey, currentCycleKey, term);
                var isDeferred = IsDeferred(originCycleKey, currentCycleKey);
                var isPastTerm = rawCyclesRemaining <= 0;
                var installments = isDeferred ? 0 : Math.Min(term, term - rawCyclesRemaining + 1);
                var previousInstallments = isDeferred ? 0 : isPastTerm ? term : installments - 1;
                var members = group.Select(cohort =>
                {
                    var remaining = Math.Max(0m, cohort.RemainingShortfall);
                    var opening = remaining + Math.Max(0m, cohort.RepaidThisCycle);
                    // Null only for a withdrawal nothing has been put back against yet.
                    var original = Math.Max(opening, cohort.OriginalAmount ?? 0m);
                    return (Remaining: remaining, Opening: opening, Original: original);
                }).ToList();
                return new CohortGroup(
                    originCycleKey,
                    group.Min(cohort => cohort.FromDate),
                    term,
                    group.Sum(cohort => Math.Max(1, cohort.TransactionCount)),
                    members.Sum(member => member.Remaining),
                    members.Sum(member => member.Original),
                    members.Sum(member => ScheduledThrough(member.Original, installments, term)),
                    members.Sum(member => ScheduledThrough(member.Original, previousInstallments, term)),
                    members.Sum(member => member.Original - member.Opening),
                    Math.Max(1, rawCyclesRemaining),
                    isPastTerm,
                    isDeferred);
            })
            .OrderBy(group => group.OriginCycleKey, StringComparer.Ordinal)
            .ThenBy(group => group.FromDate)
            .ThenBy(group => group.RepaymentCycles)
            .ToList();

        // Surplus on any plan offsets what another plan is short, oldest plan first.
        var credit = groups.Sum(group => Math.Max(0m, group.PaidBefore - group.Scheduled));
        var dueByGroup = new List<decimal>(groups.Count);
        foreach (var group in groups)
        {
            var shortBy = Math.Max(0m, group.Scheduled - group.PaidBefore);
            var offset = Math.Min(shortBy, credit);
            credit -= offset;
            dueByGroup.Add(shortBy - offset);
        }

        var requiredThisCycle = dueByGroup.Sum();
        var carried = Math.Min(
            requiredThisCycle,
            Math.Max(0m, groups.Sum(group => group.PreviousScheduled) - groups.Sum(group => group.PaidBefore)));

        // Past-term originals against everything put back so far, this cycle included: overdue only
        // when the money that has gone back cannot cover what the elapsed plans were meant to settle.
        var pastTermOriginal = groups.Where(group => group.IsPastTerm).Sum(group => group.Original);
        var paidSoFar = groups.Sum(group => group.Original - group.Remaining);
        var isOverdue = pastTermOriginal > paidSoFar;

        var paces = groups
            .Select((group, index) => new RecoveryCohortPace(
                group.OriginCycleKey,
                group.FromDate,
                group.TransactionCount,
                group.Remaining,
                group.CyclesRemaining,
                dueByGroup[index],
                isOverdue && group.IsPastTerm && group.Remaining > 0m,
                group.IsDeferred,
                group.RepaymentCycles))
            .ToList();

        var shortfall = Math.Max(0m, outstandingShortfall);
        var funded = Math.Max(0m, toppedUpThisCycle);
        var outstandingThisCycle = Math.Clamp(requiredThisCycle - funded, 0m, shortfall);
        var open = paces.Where(cohort => cohort.RemainingShortfall > 0m).ToList();
        var urgentCyclesRemaining = open.Count == 0
            ? Math.Max(1, horizon)
            : open.Min(cohort => cohort.CyclesRemaining);
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
                isDeferred,
                carried),
            paces);
    }

    public static string CycleKey(int year, int monthIndex) => $"{year:0000}-{monthIndex:00}";

    private sealed record CohortGroup(
        string OriginCycleKey,
        DateOnly FromDate,
        int RepaymentCycles,
        int TransactionCount,
        decimal Remaining,
        decimal Original,
        // Installments fallen due through this cycle, and through the one before it.
        decimal Scheduled,
        decimal PreviousScheduled,
        // Put back before this cycle began; this cycle's own repayment is credited separately.
        decimal PaidBefore,
        int CyclesRemaining,
        bool IsPastTerm,
        bool IsDeferred);

    /// <summary>
    /// Cumulative amount scheduled once <paramref name="installments"/> of <paramref name="term"/> have
    /// fallen due. Rounded up once, so installments differ by a cent at most and the last one settles
    /// the exact original amount.
    /// </summary>
    private static decimal ScheduledThrough(decimal original, int installments, int term)
    {
        if (installments <= 0) return 0m;
        if (installments >= term) return original;
        return RoundUpToCent(original * installments / term);
    }

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
