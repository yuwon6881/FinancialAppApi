using FinancialAppApi.Models;
using FinancialAppApi.Services.SavingsGoals;

namespace FinancialAppApi.Tests;

/// <summary>
/// Covers the pure deadline-to-contribution math. The client mirrors this in
/// <c>lib/savingsGoals.ts</c>, so any change here needs the matching change (and test) there.
/// </summary>
public class SavingsGoalPacingTests
{
    private const int CycleDay = 1;
    // The cycle the 15 Jul 2026 reference date falls in, with CycleDay 1.
    private const string CycleKey = "2026-07";

    [Fact]
    public void CyclesRemaining_CountsTheCurrentCycle()
    {
        var today = new DateOnly(2026, 7, 15);

        // Due inside the current cycle: this cycle is the one you have left, not zero.
        Assert.Equal(1, SavingsGoalPacing.CyclesRemaining(today, new DateOnly(2026, 7, 28), CycleDay));
        Assert.Equal(2, SavingsGoalPacing.CyclesRemaining(today, new DateOnly(2026, 8, 5), CycleDay));
        Assert.Equal(4, SavingsGoalPacing.CyclesRemaining(today, new DateOnly(2026, 10, 1), CycleDay));
    }

    [Fact]
    public void CyclesRemaining_GoesNonPositiveOnceTheDeadlineHasPassed()
    {
        var today = new DateOnly(2026, 7, 15);

        Assert.Equal(0, SavingsGoalPacing.CyclesRemaining(today, new DateOnly(2026, 6, 10), CycleDay));
        Assert.Equal(-2, SavingsGoalPacing.CyclesRemaining(today, new DateOnly(2026, 4, 10), CycleDay));
    }

    [Fact]
    public void ComputePace_SpreadsTheRemainderOverTheCyclesLeft()
    {
        // 1200 target, 400 already set aside, three cycles to go -> 800/3.
        var goal = NewGoal(target: 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));

        var pace = SavingsGoalPacing.ComputePace(goal, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        Assert.Equal(800m, pace.Remaining);
        Assert.Equal(3, pace.CyclesRemaining);
        // Rounded UP to the cent, so three of these actually clear the 800 rather than landing a
        // cent short on the deadline cycle.
        Assert.Equal(266.67m, pace.RequiredPerCycle);
        Assert.True(pace.RequiredPerCycle * 3 >= pace.Remaining);
        Assert.False(pace.IsOverdue);
        Assert.False(pace.IsFunded);
    }

    [Fact]
    public void ComputePace_DemandsTheWholeRemainderInTheDeadlineCycle()
    {
        var goal = NewGoal(target: 1000m, earmarked: 250m, targetDate: new DateOnly(2026, 7, 30));

        var pace = SavingsGoalPacing.ComputePace(goal, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        Assert.Equal(1, pace.CyclesRemaining);
        Assert.Equal(750m, pace.RequiredPerCycle);
    }

    [Fact]
    public void ComputePace_FlagsAnOverdueGoalInsteadOfSpreadingItFurther()
    {
        var goal = NewGoal(target: 500m, earmarked: 100m, targetDate: new DateOnly(2026, 5, 10));

        var pace = SavingsGoalPacing.ComputePace(goal, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        Assert.True(pace.IsOverdue);
        // The whole shortfall is due now. Silently re-spreading it would hide the missed deadline,
        // which is the one signal the feature exists to give.
        Assert.Equal(400m, pace.RequiredPerCycle);
    }

    [Fact]
    public void ComputePace_ReportsAFullyFundedGoalAsNeedingNothing()
    {
        var goal = NewGoal(target: 500m, earmarked: 500m, targetDate: new DateOnly(2026, 12, 1));

        var pace = SavingsGoalPacing.ComputePace(goal, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        Assert.True(pace.IsFunded);
        Assert.Equal(0m, pace.Remaining);
        Assert.Equal(0m, pace.RequiredPerCycle);
    }

    [Fact]
    public void ComputePace_TreatsAnOvershotEarmarkAsFundedRatherThanNegative()
    {
        // Can only arise from a lowered target racing a contribution; must not go negative.
        var goal = NewGoal(target: 300m, earmarked: 500m, targetDate: new DateOnly(2026, 12, 1));

        var pace = SavingsGoalPacing.ComputePace(goal, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        Assert.Equal(0m, pace.Remaining);
        Assert.Equal(0m, pace.RequiredPerCycle);
    }

    [Fact]
    public void OrderForFunding_PutsPriorityFirstThenTheNearestDeadline()
    {
        var lowSoon = NewGoal(id: 1, target: 100m, targetDate: new DateOnly(2026, 8, 1), priority: "Low");
        var highLater = NewGoal(id: 2, target: 100m, targetDate: new DateOnly(2030, 1, 1), priority: "High");
        var mediumSoon = NewGoal(id: 3, target: 100m, targetDate: new DateOnly(2026, 8, 5), priority: "Medium");
        var mediumSooner = NewGoal(id: 4, target: 100m, targetDate: new DateOnly(2026, 8, 2), priority: "Medium");

        var ordered = SavingsGoalPacing.OrderForFunding([lowSoon, highLater, mediumSoon, mediumSooner]);

        // High wins even with a deadline four years out -- that is what marking it High is for.
        // Within a priority band the nearer deadline goes first.
        Assert.Equal([2, 4, 3, 1], ordered.Select(goal => goal.Id).ToArray());
    }

    [Fact]
    public void Distribute_FillsCommitmentsBeforeLeavingAnythingFreeToSpend()
    {
        var carService = NewGoal(id: 1, target: 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));
        var houseFund = NewGoal(id: 2, target: 60000m, earmarked: 2000m, targetDate: new DateOnly(2032, 7, 1));

        var result = SavingsGoalPacing.Distribute([carService, houseFund], 1200m, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        var car = result.Grants.Single(grant => grant.GoalId == 1);
        var house = result.Grants.Single(grant => grant.GoalId == 2);
        // Each goal is capped at its own pace: 800 over 3 cycles, and 58000 over 73 cycles.
        Assert.Equal(266.67m, car.Amount);
        Assert.Equal(0m, car.Shortfall);
        Assert.Equal(794.53m, house.Amount);
        Assert.Equal(0m, house.Shortfall);
        Assert.Equal(1061.20m, result.TotalGranted);
        // Both commitments were met in full, so the rest stays spontaneous-reward money.
        Assert.Equal(138.80m, result.FreeToSpend);
        Assert.Equal(0m, result.Shortfall);
    }

    [Fact]
    public void Distribute_LeavesTheRemainderFreeRatherThanOverfundingTheFirstGoal()
    {
        var goal = NewGoal(id: 1, target: 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));

        var result = SavingsGoalPacing.Distribute([goal], 1000m, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        Assert.Equal(266.67m, result.TotalGranted);
        // The surplus is spontaneous-reward money, not a silent extra contribution.
        Assert.Equal(733.33m, result.FreeToSpend);
    }

    [Fact]
    public void Distribute_ReportsAShortfallWhenTheGoalsWantMoreThanTheCycleHas()
    {
        var carService = NewGoal(id: 1, target: 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));
        var houseFund = NewGoal(id: 2, target: 60000m, earmarked: 2000m, targetDate: new DateOnly(2032, 7, 1));

        var result = SavingsGoalPacing.Distribute([carService, houseFund], 800m, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        Assert.Equal(800m, result.TotalGranted);
        Assert.Equal(0m, result.FreeToSpend);
        Assert.True(result.Shortfall > 0m);
        // The nearer, equally-prioritised deadline was covered; the long-horizon fund absorbed the gap.
        Assert.Equal(0m, result.Grants.Single(grant => grant.GoalId == 1).Shortfall);
        Assert.True(result.Grants.Single(grant => grant.GoalId == 2).Shortfall > 0m);
    }

    [Fact]
    public void Distribute_NeverGrantsPastTheTarget()
    {
        // Due this cycle and 50 short, with far more money available than it needs.
        var goal = NewGoal(id: 1, target: 500m, earmarked: 450m, targetDate: new DateOnly(2026, 7, 30));

        var result = SavingsGoalPacing.Distribute([goal], 5000m, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        Assert.Equal(50m, result.TotalGranted);
        Assert.Equal(4950m, result.FreeToSpend);
    }

    [Fact]
    public void Distribute_HandlesAnEmptyPoolAndNoGoals()
    {
        var goal = NewGoal(id: 1, target: 500m, targetDate: new DateOnly(2026, 12, 1));

        var broke = SavingsGoalPacing.Distribute([goal], 0m, new DateOnly(2026, 7, 15), CycleDay, CycleKey);
        Assert.Equal(0m, broke.TotalGranted);
        Assert.Equal(0m, broke.FreeToSpend);
        Assert.True(broke.Shortfall > 0m);

        var noGoals = SavingsGoalPacing.Distribute([], 250m, new DateOnly(2026, 7, 15), CycleDay, CycleKey);
        Assert.Empty(noGoals.Grants);
        Assert.Equal(250m, noGoals.FreeToSpend);
        Assert.Equal(0m, noGoals.Shortfall);
    }

    [Fact]
    public void Distribute_ClampsANegativeAvailableAmountToZero()
    {
        // A Rewards balance that has dropped below the outstanding earmarks must not turn into a
        // negative pool that then reads as "money available".
        var goal = NewGoal(id: 1, target: 500m, targetDate: new DateOnly(2026, 12, 1));

        var result = SavingsGoalPacing.Distribute([goal], -300m, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        Assert.Equal(0m, result.TotalGranted);
        Assert.Equal(0m, result.FreeToSpend);
    }

    [Fact]
    public void OutstandingThisCycle_IgnoresATallyFromAnEarlierCycle()
    {
        var goal = NewGoal(target: 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));
        goal.CycleFundedKey = "2026-06";
        goal.CycleFundedAmount = 266.67m;
        var pace = SavingsGoalPacing.ComputePace(goal, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        // Last cycle's contribution buys nothing this cycle.
        Assert.Equal(266.67m, pace.OutstandingThisCycle);
    }

    [Fact]
    public void OutstandingThisCycle_IsZeroOnceThisCyclesPaceIsMet()
    {
        var goal = NewGoal(target: 1200m, earmarked: 666.67m, targetDate: new DateOnly(2026, 9, 20));
        goal.CycleFundedKey = CycleKey;
        goal.CycleFundedAmount = 266.67m;
        var pace = SavingsGoalPacing.ComputePace(goal, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        // Already paced this cycle -- "Fund this cycle" must not contribute again.
        Assert.Equal(0m, pace.OutstandingThisCycle);
        // The pace is measured from the cycle's starting position, so it does not shrink as the goal
        // fills: the figure on the card holds still while money goes in.
        Assert.Equal(266.67m, pace.RequiredPerCycle);
    }

    [Fact]
    public void OutstandingThisCycle_ReopensAfterAPartialRelease()
    {
        // Funded 266.67 then released 100, so the tally is the net 166.67.
        var goal = NewGoal(target: 1200m, earmarked: 566.67m, targetDate: new DateOnly(2026, 9, 20));
        goal.CycleFundedKey = CycleKey;
        goal.CycleFundedAmount = 166.67m;
        var pace = SavingsGoalPacing.ComputePace(goal, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        // Exactly the released amount becomes fundable again -- not the whole per-cycle pace.
        Assert.Equal(100m, pace.OutstandingThisCycle);
    }

    [Fact]
    public void OutstandingThisCycle_NeverExceedsWhatTheGoalStillNeeds()
    {
        // 50 short overall, but a nominal per-cycle pace far larger than that.
        var goal = NewGoal(target: 500m, earmarked: 450m, targetDate: new DateOnly(2026, 7, 30));
        var pace = SavingsGoalPacing.ComputePace(goal, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        Assert.Equal(50m, pace.OutstandingThisCycle);
    }

    [Fact]
    public void Distribute_SkipsGoalsAlreadyFundedThisCycleAndFundsOnlyTheRest()
    {
        var alreadyFunded = NewGoal(id: 1, target: 1200m, earmarked: 666.67m, targetDate: new DateOnly(2026, 9, 20));
        alreadyFunded.CycleFundedKey = CycleKey;
        alreadyFunded.CycleFundedAmount = 266.67m;
        var untouched = NewGoal(id: 2, target: 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));

        var result = SavingsGoalPacing.Distribute([alreadyFunded, untouched], 5000m, new DateOnly(2026, 7, 15), CycleDay, CycleKey);

        Assert.Equal(0m, result.Grants.Single(grant => grant.GoalId == 1).Amount);
        Assert.Equal(266.67m, result.Grants.Single(grant => grant.GoalId == 2).Amount);
        Assert.Equal(266.67m, result.TotalGranted);
    }

    [Fact]
    public void Unassigned_FloorsAtZeroWhenEarmarksExceedTheBalance()
    {
        Assert.Equal(600m, SavingsGoalPacing.Unassigned(3000m, 2400m));
        Assert.Equal(0m, SavingsGoalPacing.Unassigned(1000m, 2400m));
        Assert.Equal(0m, SavingsGoalPacing.Unassigned(-50m, 0m));
    }

    private static SavingsGoal NewGoal(
        decimal target,
        DateOnly targetDate,
        decimal earmarked = 0m,
        int id = 1,
        string priority = "Medium")
    {
        return new SavingsGoal
        {
            Id = id,
            Name = $"Goal {id}",
            TargetAmount = target,
            EarmarkedAmount = earmarked,
            TargetDate = targetDate.ToDateTime(TimeOnly.MinValue),
            Priority = priority,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }
}
