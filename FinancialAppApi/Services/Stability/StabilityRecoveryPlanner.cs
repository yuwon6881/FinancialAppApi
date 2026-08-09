namespace FinancialAppApi.Services.Stability;

/// <summary>
/// Where the emergency fund stands against the highest point it has ever reached.
/// <para>
/// <c>HighWaterMark</c> is the largest cycle-ending Stability balance on record, raised to the live
/// balance so an all-time high never reports a shortfall. <c>RecoverableCeiling</c> is
/// <c>min(HighWaterMark, Target)</c> -- and that min, not the target, is what recovery aims at. It
/// is what separates "you spent it" from "you never finished filling it": someone who has only ever
/// reached 2,000 of a 10,000 target is at their own high-water mark and is asked for nothing, since
/// the ordinary Stability share is already doing that job. It also means raising the target
/// mid-recovery cannot invent a drawdown that never happened.
/// </para>
/// <para>
/// <c>OutstandingShortfall</c> is the ceiling minus the live balance, floored at zero.
/// <c>LastDrawdownCycleKey</c> is "yyyy-MM" of the most recent cycle the fund fell in, or null.
/// </para>
/// <para>
/// There is deliberately no "shortfall at cycle start" here any more. It was the obvious anchor for
/// the pace and it was wrong: when the drawdown happens during the current cycle, the cycle opened
/// with no shortfall at all, so the plan asked for nothing in the very cycle the money was spent
/// and the card stayed hidden. See <see cref="StabilityRecoveryPlanner.ComputePace"/>.
/// </para>
/// </summary>
public sealed record StabilityDrawdown(
    decimal HighWaterMark,
    decimal Target,
    decimal RecoverableCeiling,
    decimal CurrentBalance,
    decimal OutstandingShortfall,
    string? LastDrawdownCycleKey,
    decimal LastDrawdownAmount)
{
    public bool IsActive => OutstandingShortfall > 0m;
}

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

/// <summary>One bucket's contribution to a proposed top-up.</summary>
public sealed record BucketDraw(string Bucket, decimal Share, decimal Amount);

/// <summary>
/// A bucket as the planner needs to see it: what proportion of income it receives, what it holds,
/// and how much of that is already promised to something else this cycle.
/// <para>
/// <c>Committed</c> is money this cycle has already committed -- bills still to pay for Essentials,
/// active goal earmarks plus funding still owed for Rewards. Growth passes zero: it has no hard per-cycle
/// obligation the way the other two do.
/// </para>
/// </summary>
public sealed record BucketState(string Bucket, decimal Alloc, decimal Balance, decimal Committed);

/// <summary>
/// What a specific income amount can actually do about the shortfall. <c>ProposedTopUp</c> is the
/// offer after every cap, zero meaning there is nothing to offer; <c>IsReduced</c> is true when a
/// committed-money floor cut it below what the pace asked for, and <c>LimitedBy</c> names the
/// bucket whose floor bound it.
/// </summary>
public sealed record RecoveryOffer(
    decimal RequestedTopUp,
    decimal ProposedTopUp,
    bool IsReduced,
    string? LimitedBy,
    IReadOnlyList<BucketDraw> Draws);

/// <summary>
/// Pure emergency-fund recovery math: how far the fund has fallen from its own high-water mark,
/// how much of that to ask back each cycle, and how much of a given salary can safely provide it.
/// Kept free of EF and clocks -- like <see cref="SavingsGoals.SavingsGoalPacing"/>, which it
/// deliberately echoes -- and mirrored on the client in <c>lib/stabilityRecovery.ts</c>.
/// </summary>
public static class StabilityRecoveryPlanner
{
    public static StabilityDrawdown ComputeDrawdown(
        decimal highWaterMark,
        decimal target,
        decimal currentBalance,
        string? lastDrawdownCycleKey,
        decimal lastDrawdownAmount)
    {
        var mark = Math.Max(highWaterMark, currentBalance);
        // A non-positive target means "no target set", not "aim at nothing" -- taking the min
        // against zero would silently switch recovery off for anyone who never picked a figure,
        // which is the same trap IncomeSplitPlanner guards on the cap side. With no target the
        // fund's own high-water mark is the only thing to aim at, and it is enough: the ask is
        // still only ever money that was really in there.
        var ceiling = target > 0m ? Math.Min(mark, target) : mark;

        return new StabilityDrawdown(
            mark,
            target,
            ceiling,
            currentBalance,
            Math.Max(0m, ceiling - currentBalance),
            lastDrawdownCycleKey,
            Math.Max(0m, lastDrawdownAmount));
    }

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

    public static RecoveryPace ComputePace(StabilityDrawdown drawdown, int cyclesRemaining, decimal toppedUpThisCycle)
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
        var anchor = drawdown.OutstandingShortfall + funded;
        var required = cycles <= 1 ? anchor : RoundUpToCent(anchor / cycles);

        // Capped by the live shortfall too, so the last stretch only asks for what is actually left.
        var outstanding = Math.Clamp(required - funded, 0m, drawdown.OutstandingShortfall);

        return new RecoveryPace(
            drawdown.OutstandingShortfall, cycles, required, funded, outstanding, cyclesRemaining <= 0);
    }

    /// <summary>
    /// How much of <paramref name="incomeAmount"/> can go back into the fund on top of its usual
    /// share, and where it comes from.
    /// <para>
    /// The draw is proportional to the three buckets' configured allocations, so no single pot
    /// takes the whole hit. It is then held below every bucket's committed money -- bills for
    /// Essentials, savings goals for Rewards -- because refilling a buffer with money that is
    /// already promised elsewhere just moves the problem.
    /// </para>
    /// </summary>
    public static RecoveryOffer ProposeTopUp(
        RecoveryPace pace,
        decimal incomeAmount,
        IReadOnlyList<BucketState> buckets,
        decimal stabilityAlloc = 0m)
    {
        var empty = new RecoveryOffer(0m, 0m, false, null, Array.Empty<BucketDraw>());
        if (incomeAmount <= 0m || pace.OutstandingThisCycle <= 0m) return empty;

        var contributing = buckets.Where(bucket => bucket.Alloc > 0m).ToList();
        var allocTotal = contributing.Sum(bucket => bucket.Alloc);
        if (allocTotal <= 0m) return empty;

        var remainingAfterNormal = Math.Max(
            0m,
            pace.Shortfall - incomeAmount * Math.Max(0m, stabilityAlloc));
        if (remainingAfterNormal <= 0m) return empty;

        // Cannot draw more than what remains after the normal Stability share, or than the three
        // other buckets actually receive.
        var requested = Math.Min(pace.OutstandingThisCycle,
            Math.Min(remainingAfterNormal, incomeAmount * allocTotal));

        var cap = requested;
        string? limitedBy = null;
        foreach (var bucket in contributing)
        {
            var headroom = Math.Max(0m, bucket.Balance + incomeAmount * bucket.Alloc - bucket.Committed);
            // Multiplied through by allocTotal rather than divided by a pre-computed weight: an
            // allocation like 0.50/0.85 is a repeating decimal, and dividing by the rounded result
            // lands a cent under, which the floor below then makes permanent.
            var bucketCap = headroom * allocTotal / bucket.Alloc;
            if (bucketCap < cap)
            {
                cap = bucketCap;
                limitedBy = bucket.Bucket;
            }
        }

        // Floored, not rounded up: this is a ceiling on how much may be moved, and rounding a
        // ceiling up breaks the very invariant it exists to protect. The per-cycle requirement
        // rounds up (so N cycles always arrive); the per-salary offer rounds down.
        var proposed = FloorToCent(Math.Max(0m, cap));
        if (proposed <= 0m)
        {
            return new RecoveryOffer(requested, 0m, requested > 0m, limitedBy, Array.Empty<BucketDraw>());
        }

        var draws = BuildDraws(contributing, allocTotal, proposed);
        return new RecoveryOffer(requested, proposed, proposed < requested, limitedBy, draws);
    }

    /// <summary>
    /// Splits the proposal across the contributing buckets by weight, pushing the rounding
    /// remainder onto the largest draw so the parts sum to the whole exactly.
    /// </summary>
    private static IReadOnlyList<BucketDraw> BuildDraws(
        IReadOnlyList<BucketState> contributing,
        decimal allocTotal,
        decimal proposed)
    {
        var draws = contributing
            .Select(bucket => new BucketDraw(
                bucket.Bucket,
                bucket.Alloc / allocTotal,
                FloorToCent(proposed * bucket.Alloc / allocTotal)))
            .ToList();

        var remainder = proposed - draws.Sum(draw => draw.Amount);
        if (remainder == 0m) return draws;

        var largest = 0;
        for (var i = 1; i < draws.Count; i++)
        {
            if (draws[i].Amount > draws[largest].Amount) largest = i;
        }
        draws[largest] = draws[largest] with { Amount = draws[largest].Amount + remainder };
        return draws;
    }

    /// <summary>
    /// Compatibility inference for a current-cycle legacy salary whose intent field is null.
    /// New salaries use their persisted applied reimbursement instead, so later allocation changes
    /// cannot rewrite recovery history.
    /// </summary>
    public static decimal ToppedUpThisCycle(decimal stabilityCredit, decimal incomeThisCycle, decimal stabilityAlloc)
    {
        return Math.Max(0m, stabilityCredit - incomeThisCycle * stabilityAlloc);
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

    private static decimal FloorToCent(decimal value) => Math.Floor(value * 100m) / 100m;
}
