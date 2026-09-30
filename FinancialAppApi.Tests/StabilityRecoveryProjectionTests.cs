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

        Assert.Equal([133.34m, 116.66m, 50m], schedule.Select(cycle => cycle.PutBack));
        Assert.Equal(300m, schedule.Sum(cycle => cycle.PutBack));
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
