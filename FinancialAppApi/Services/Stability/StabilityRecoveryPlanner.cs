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
/// <c>OutstandingShortfall</c> is the ceiling minus the live balance, floored at zero;
/// <c>ShortfallAtCycleStart</c> is the same against the cycle's opening balance, which is what the
/// pace is derived from so the requirement holds still as it is funded.
/// <c>LastDrawdownCycleKey</c> is "yyyy-MM" of the most recent cycle the fund fell in, or null.
/// </para>
/// </summary>
public sealed record StabilityDrawdown(
    decimal HighWaterMark,
    decimal Target,
    decimal RecoverableCeiling,
    decimal CurrentBalance,
    decimal BalanceAtCycleStart,
    decimal OutstandingShortfall,
    decimal ShortfallAtCycleStart,
    string? LastDrawdownCycleKey,
    decimal LastDrawdownAmount)
{
    public bool IsActive => OutstandingShortfall > 0m;
}

/// <summary>
/// What this cycle asks for, before any particular income amount is known.
/// <para>
/// <c>CyclesRemaining</c> counts this cycle and never falls below 1: a window that has run out
/// still owes its remainder, exactly as an overdue savings goal does. <c>ToppedUpThisCycle</c> is
/// Stability credit received this cycle beyond what the plain allocation percentage would have
/// delivered, derived from the ledger rather than stored, so deleting the salary reopens the ask.
/// </para>
/// </summary>
public sealed record RecoveryPace(
    decimal Shortfall,
    int CyclesRemaining,
    decimal RequiredThisCycle,
    decimal ToppedUpThisCycle,
    decimal OutstandingThisCycle);

/// <summary>One bucket's contribution to a proposed top-up.</summary>
public sealed record BucketDraw(string Bucket, decimal Share, decimal Amount);

/// <summary>
/// A bucket as the planner needs to see it: what proportion of income it receives, what it holds,
/// and how much of that is already promised to something else this cycle.
/// <para>
/// <c>Committed</c> is money this cycle has already committed -- bills still to pay for Essentials,
/// savings-goal funding still owed for Rewards. Growth passes zero: it has no hard per-cycle
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
        decimal balanceAtCycleStart,
        string? lastDrawdownCycleKey,
        decimal lastDrawdownAmount)
    {
        var mark = Math.Max(highWaterMark, currentBalance);
        var ceiling = Math.Min(mark, target);

        return new StabilityDrawdown(
            mark,
            target,
            ceiling,
            currentBalance,
            balanceAtCycleStart,
            Math.Max(0m, ceiling - currentBalance),
            Math.Max(0m, ceiling - balanceAtCycleStart),
            lastDrawdownCycleKey,
            Math.Max(0m, lastDrawdownAmount));
    }

    /// <summary>
    /// Cycles left in the recovery window, counting the current one. Floored at 1 so the final
    /// cycle asks for the whole remainder instead of dividing by zero, and so a window that has
    /// already elapsed keeps asking rather than silently giving up.
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
        return Math.Max(1, horizon - Math.Max(0, elapsed));
    }

    public static RecoveryPace ComputePace(StabilityDrawdown drawdown, int cyclesRemaining, decimal toppedUpThisCycle)
    {
        var funded = Math.Max(0m, toppedUpThisCycle);
        var cycles = Math.Max(1, cyclesRemaining);

        // Measured from the cycle's OPENING balance, the same reason SavingsGoalPacing measures
        // from remainingAtCycleStart: using the live balance would shrink the ask the instant money
        // went in, so the figure on screen would move as you funded it and the cycle could never be
        // "done".
        var required = cycles <= 1
            ? drawdown.ShortfallAtCycleStart
            : RoundUpToCent(drawdown.ShortfallAtCycleStart / cycles);

        // Capped by the live shortfall too, so the last stretch only asks for what is actually left.
        var outstanding = Math.Clamp(required - funded, 0m, drawdown.OutstandingShortfall);

        return new RecoveryPace(drawdown.OutstandingShortfall, cycles, required, funded, outstanding);
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
        IReadOnlyList<BucketState> buckets)
    {
        var empty = new RecoveryOffer(0m, 0m, false, null, Array.Empty<BucketDraw>());
        if (incomeAmount <= 0m || pace.OutstandingThisCycle <= 0m) return empty;

        var contributing = buckets.Where(bucket => bucket.Alloc > 0m).ToList();
        var allocTotal = contributing.Sum(bucket => bucket.Alloc);
        if (allocTotal <= 0m) return empty;

        // Cannot draw more than the three buckets are actually going to receive.
        var requested = Math.Min(pace.OutstandingThisCycle, incomeAmount * allocTotal);

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
    /// Stability credit this cycle beyond the plain allocation percentage. Derived rather than
    /// stored: an accepted top-up IS a ledger row, unlike a savings-goal earmark, so a stored
    /// marker would be a second source of truth that a transaction delete would not update.
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
