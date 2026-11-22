using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Tests;

/// <summary>
/// The forward schedule Ask AI quotes for "how much do I put back, and when". It must be the
/// planner's own answer cycle after cycle -- a withdrawal is spread over the three cycles after the
/// one it left in -- never "everything owed, next cycle".
/// </summary>
public class StabilityRecoveryProjectionTests
{
    [Fact]
    public void OneWithdrawalIsSpreadOverTheThreeCyclesAfterItLeft()
    {
        var schedule = StabilityRecoveryProjection.Project(
            [new RecoveryCohortInput("2026-09", new DateOnly(2026, 9, 29), 1, 200m, 0m)],
            firstCycleKey: "2026-10",
            horizon: 3,
            maxCycles: 6);

        Assert.Equal(["2026-10", "2026-11", "2026-12"], schedule.Select(cycle => cycle.CycleKey));
        Assert.Equal([66.67m, 66.67m, 66.66m], schedule.Select(cycle => cycle.PutBack));
        Assert.Equal(0m, schedule[^1].StillOwedAfter);
    }

    [Fact]
    public void OverlappingWindowsAddUpAndRepaymentGoesOldestFirst()
    {
        // The August cohort has two cycles left in October; September's opens in October.
        var schedule = StabilityRecoveryProjection.Project(
        [
            new RecoveryCohortInput("2026-08", new DateOnly(2026, 8, 28), 1, 200m, 0m),
            new RecoveryCohortInput("2026-09", new DateOnly(2026, 9, 27), 1, 100m, 0m)
        ],
        firstCycleKey: "2026-10",
        horizon: 3,
        maxCycles: 6);

        Assert.Equal([166.68m, 99.99m, 33.33m], schedule.Select(cycle => cycle.PutBack));
        Assert.Equal(300m, schedule.Sum(cycle => cycle.PutBack));
    }

    [Fact]
    public void PayingEveryAskInFullNeverLeavesAShortPlanOverdue()
    {
        // The repayment is applied oldest-first, so the six-cycle plan absorbs money the one-cycle plan
        // needed. Judged plan by plan, the newer plan then read as overdue for four cycles while its owner
        // paid every ask. The pooled ask stays on schedule and clears both plans with the older term.
        var cohorts = new[]
        {
            new RecoveryCohortInput("2026-01", new DateOnly(2026, 1, 5), 1, 600m, 0m, 6),
            new RecoveryCohortInput("2026-02", new DateOnly(2026, 2, 5), 1, 100m, 0m, 1)
        };
        var schedule = StabilityRecoveryProjection.Project(cohorts, firstCycleKey: "2026-02", horizon: 3, maxCycles: 12);

        Assert.Equal([100m, 200m, 100m, 100m, 100m, 100m], schedule.Select(cycle => cycle.PutBack));
        Assert.Equal("2026-07", schedule[^1].CycleKey);
        Assert.Equal(0m, schedule[^1].StillOwedAfter);

        // Re-price cycle 4 after the first two asks: the one-cycle plan is past its term and unpaid, yet
        // the money already put back covers everything that was due, so nothing is overdue.
        var beforeFourth = StabilityRecoveryProjection.Discharge(
            StabilityRecoveryProjection.Discharge(cohorts, 100m), 200m);
        var plan = StabilityRecoveryPlanner.ComputeCohortPlan(beforeFourth, "2026-04", 3, 400m, 0m);
        Assert.False(plan.Aggregate.IsOverdue);
        Assert.Equal(100m, plan.Aggregate.RequiredThisCycle);
    }

    [Fact]
    public void AnOverdueWindowAsksForItsWholeRemainder()
    {
        var schedule = StabilityRecoveryProjection.Project(
            [new RecoveryCohortInput("2026-05", new DateOnly(2026, 5, 28), 1, 90m, 0m)],
            firstCycleKey: "2026-10",
            horizon: 3,
            maxCycles: 6);

        var only = Assert.Single(schedule);
        Assert.Equal(90m, only.PutBack);
    }

    [Fact]
    public void DischargeRepaysTheOldestCohortFirstAndDropsSettledOnes()
    {
        var left = StabilityRecoveryProjection.Discharge(
        [
            new RecoveryCohortInput("2026-09", new DateOnly(2026, 9, 27), 1, 100m, 0m),
            new RecoveryCohortInput("2026-08", new DateOnly(2026, 8, 28), 1, 50m, 0m)
        ], 80m);

        var remaining = Assert.Single(left);
        Assert.Equal("2026-09", remaining.OriginCycleKey);
        Assert.Equal(70m, remaining.RemainingShortfall);
    }

    [Theory]
    [InlineData("2026-09", "2026-10")]
    [InlineData("2026-12", "2027-01")]
    public void NextCycleKeyRollsOverTheYear(string key, string next) =>
        Assert.Equal(next, StabilityRecoveryProjection.NextCycleKey(key));
}
