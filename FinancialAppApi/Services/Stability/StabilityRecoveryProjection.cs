namespace FinancialAppApi.Services.Stability;

/// <summary>One future cycle of a recovery schedule: the ask, and what is still owed after it.</summary>
public sealed record RecoveryProjectionCycle(string CycleKey, decimal PutBack, decimal StillOwedAfter);

/// <summary>
/// What the recovery plan will ask for in the cycles ahead, if each ask is put back in full and the
/// fund moves in no other way. Every cycle is priced by <see cref="StabilityRecoveryPlanner.ComputeCohortPlan"/>
/// and its ask is discharged oldest-first, as a real top-up is, so the schedule is the planner's own
/// answer rather than a second implementation of the three-cycle rule.
/// </summary>
public static class StabilityRecoveryProjection
{
    public static IReadOnlyList<RecoveryProjectionCycle> Project(
        IEnumerable<RecoveryCohortInput> cohorts,
        string firstCycleKey,
        int horizon,
        int maxCycles)
    {
        // Each projected cycle starts fresh: nothing has been put back in it yet.
        var open = Ordered(cohorts).Select(cohort => cohort with { RepaidThisCycle = 0m }).ToList();
        var schedule = new List<RecoveryProjectionCycle>();
        var cycleKey = firstCycleKey;
        while (schedule.Count < maxCycles && open.Count > 0)
        {
            var owed = open.Sum(cohort => cohort.RemainingShortfall);
            var plan = StabilityRecoveryPlanner.ComputeCohortPlan(open, cycleKey, horizon, owed, 0m);
            var ask = Math.Min(owed, plan.Aggregate.RequiredThisCycle);
            open = Discharge(open, ask).ToList();
            schedule.Add(new RecoveryProjectionCycle(cycleKey, ask, owed - ask));
            cycleKey = NextCycleKey(cycleKey);
        }
        return schedule;
    }

    /// <summary>Applies a repayment oldest cohort first, the order the reload queue uses.</summary>
    public static IReadOnlyList<RecoveryCohortInput> Discharge(IEnumerable<RecoveryCohortInput> cohorts, decimal amount)
    {
        var left = Math.Max(0m, amount);
        var result = new List<RecoveryCohortInput>();
        foreach (var cohort in Ordered(cohorts))
        {
            var applied = Math.Min(cohort.RemainingShortfall, left);
            left -= applied;
            if (cohort.RemainingShortfall - applied > 0m)
                result.Add(cohort with { RemainingShortfall = cohort.RemainingShortfall - applied });
        }
        return result;
    }

    public static string NextCycleKey(string cycleKey)
    {
        var year = int.Parse(cycleKey[..4]);
        var month = int.Parse(cycleKey[5..]);
        return month == 12
            ? StabilityRecoveryPlanner.CycleKey(year + 1, 1)
            : StabilityRecoveryPlanner.CycleKey(year, month + 1);
    }

    private static IEnumerable<RecoveryCohortInput> Ordered(IEnumerable<RecoveryCohortInput> cohorts) =>
        cohorts
            .Where(cohort => cohort.RemainingShortfall > 0m)
            .OrderBy(cohort => cohort.OriginCycleKey, StringComparer.Ordinal)
            .ThenBy(cohort => cohort.FromDate);
}
