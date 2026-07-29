using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.SavingsGoals;

namespace FinancialAppApi.Tests;

/// <summary>
/// Covers the pool-wide earmark invariant (SUM(earmarked) &lt;= Rewards balance) and the goal
/// lifecycle. These are the rules that keep a goal from laying claim to money that is not there.
/// </summary>
public class SavingsGoalServiceTests
{
    private static readonly DateOnly Today = new(2026, 7, 15);

    [Fact]
    public async Task GetPoolSummaryAsync_SplitsTheRewardsBalanceIntoEarmarkedAndFree()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        context.SavingsGoals.Add(NewGoal("Car service", 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20)));
        context.SavingsGoals.Add(NewGoal("House deposit", 60000m, earmarked: 2000m, targetDate: new DateOnly(2032, 7, 1)));
        await context.SaveChangesAsync();

        var summary = await NewService(context).GetPoolSummaryAsync();

        Assert.Equal(3000m, summary.RewardsBalance);
        Assert.Equal(2400m, summary.TotalEarmarked);
        // This -- not the 3000 -- is what a wishlist reward can actually be claimed against.
        Assert.Equal(600m, summary.Unassigned);
        Assert.Equal("2026-07", summary.CurrentCycleKey);
        Assert.True(summary.RequiredPerCycleTotal > 0m);
    }

    [Fact]
    public async Task GetPoolSummaryAsync_IgnoresCompletedGoals()
    {
        await using var context = NewContext(rewardsBalance: 1000m);
        var done = NewGoal("Old goal", 500m, earmarked: 0m, targetDate: new DateOnly(2026, 6, 1));
        done.Status = SavingsGoalStatus.Completed;
        context.SavingsGoals.Add(done);
        await context.SaveChangesAsync();

        var summary = await NewService(context).GetPoolSummaryAsync();

        Assert.Equal(0m, summary.TotalEarmarked);
        Assert.Equal(1000m, summary.Unassigned);
        Assert.Equal(0m, summary.RequiredPerCycleTotal);
    }

    [Fact]
    public async Task ContributeAsync_RejectsATopUpThatWouldClaimMoreThanThePoolHolds()
    {
        await using var context = NewContext(rewardsBalance: 500m);
        var goal = NewGoal("Car service", 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        // Only 100 of the 500 is unassigned; asking for 300 must fail rather than over-commit.
        var result = await NewService(context).ContributeAsync(goal.Id, 300m);

        Assert.Equal(SavingsGoalMutationStatus.ExceedsAvailable, result.Status);
        Assert.Equal(400m, context.SavingsGoals.Single().EarmarkedAmount);
    }

    [Fact]
    public async Task ContributeAsync_AllowsATopUpThatExactlyConsumesTheFreeRemainder()
    {
        await using var context = NewContext(rewardsBalance: 500m);
        var goal = NewGoal("Car service", 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var result = await NewService(context).ContributeAsync(goal.Id, 100m);

        Assert.Equal(SavingsGoalMutationStatus.Success, result.Status);
        Assert.Equal(500m, context.SavingsGoals.Single().EarmarkedAmount);
    }

    [Fact]
    public async Task ContributeAsync_NeverEarmarksPastTheGoalTarget()
    {
        await using var context = NewContext(rewardsBalance: 5000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 1100m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var result = await NewService(context).ContributeAsync(goal.Id, 900m);

        Assert.Equal(SavingsGoalMutationStatus.Success, result.Status);
        // Holding money the goal does not need would starve everything else for no reason.
        Assert.Equal(1200m, context.SavingsGoals.Single().EarmarkedAmount);
    }

    [Fact]
    public async Task ContributeAsync_ReleasesMoneyBackToThePoolAndCannotGoNegative()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();
        var service = NewService(context);

        Assert.Equal(SavingsGoalMutationStatus.Success, (await service.ContributeAsync(goal.Id, -150m)).Status);
        Assert.Equal(250m, context.SavingsGoals.Single().EarmarkedAmount);

        Assert.Equal(SavingsGoalMutationStatus.Success, (await service.ContributeAsync(goal.Id, -9999m)).Status);
        Assert.Equal(0m, context.SavingsGoals.Single().EarmarkedAmount);
    }

    [Fact]
    public async Task ContributeAsync_RejectsAZeroAmountAndACompletedGoal()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var active = NewGoal("Car service", 1200m, earmarked: 0m, targetDate: new DateOnly(2026, 9, 20));
        var done = NewGoal("Old goal", 500m, earmarked: 0m, targetDate: new DateOnly(2026, 6, 1));
        done.Status = SavingsGoalStatus.Completed;
        context.SavingsGoals.AddRange(active, done);
        await context.SaveChangesAsync();
        var service = NewService(context);

        Assert.Equal(SavingsGoalMutationStatus.ContributionInvalid, (await service.ContributeAsync(active.Id, 0m)).Status);
        Assert.Equal(SavingsGoalMutationStatus.AlreadyCompleted, (await service.ContributeAsync(done.Id, 50m)).Status);
    }

    [Fact]
    public async Task CreateGoalAsync_RejectsASeededEarmarkThatDoesNotFitInThePool()
    {
        await using var context = NewContext(rewardsBalance: 100m);

        var result = await NewService(context).CreateGoalAsync(
            NewGoal("Car service", 1200m, earmarked: 500m, targetDate: new DateOnly(2026, 9, 20)));

        Assert.Equal(SavingsGoalMutationStatus.ExceedsAvailable, result.Status);
        Assert.Empty(context.SavingsGoals);
    }

    [Fact]
    public async Task CreateGoalAsync_DedupesAReplayedOfflineCreateOnTheClientKey()
    {
        await using var context = NewContext(rewardsBalance: 1000m);
        var service = NewService(context);
        var first = await service.CreateGoalAsync(NewGoal("Car service", 1200m, targetDate: new DateOnly(2026, 9, 20), clientKey: "op-1"));

        var replay = await service.CreateGoalAsync(NewGoal("Car service", 1200m, targetDate: new DateOnly(2026, 9, 20), clientKey: "op-1"));

        Assert.Equal(SavingsGoalMutationStatus.Success, replay.Status);
        Assert.Equal(first.Goal!.Id, replay.Goal!.Id);
        Assert.Single(context.SavingsGoals);
    }

    [Theory]
    [InlineData("", 1200, "2026-09-20", SavingsGoalMutationStatus.NameRequired)]
    [InlineData("Car service", 0, "2026-09-20", SavingsGoalMutationStatus.TargetInvalid)]
    [InlineData("Car service", -50, "2026-09-20", SavingsGoalMutationStatus.TargetInvalid)]
    [InlineData("Car service", 1200, null, SavingsGoalMutationStatus.TargetDateInvalid)]
    public async Task CreateGoalAsync_ValidatesTheGoalShape(
        string name,
        decimal target,
        string? targetDate,
        SavingsGoalMutationStatus expected)
    {
        await using var context = NewContext(rewardsBalance: 1000m);
        var goal = new SavingsGoal
        {
            Name = name,
            TargetAmount = target,
            TargetDate = targetDate == null ? default : DateTime.Parse(targetDate)
        };

        var result = await NewService(context).CreateGoalAsync(goal);

        Assert.Equal(expected, result.Status);
        Assert.Empty(context.SavingsGoals);
    }

    [Fact]
    public async Task UpdateGoalAsync_ReleasesTheSurplusWhenTheTargetIsLowered()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 900m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var result = await NewService(context).UpdateGoalAsync(goal.Id, new SavingsGoal
        {
            Id = goal.Id,
            Name = "Car service",
            TargetAmount = 600m,
            TargetDate = goal.TargetDate,
            Priority = "Medium"
        });

        Assert.Equal(SavingsGoalMutationStatus.Success, result.Status);
        // The 300 above the new target goes back to being free rather than being held hostage.
        Assert.Equal(600m, context.SavingsGoals.Single().EarmarkedAmount);
    }

    [Fact]
    public async Task UpdateGoalAsync_LeavesTheEarmarkAloneWhenTheTargetIsRaised()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 900m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        await NewService(context).UpdateGoalAsync(goal.Id, new SavingsGoal
        {
            Id = goal.Id,
            Name = "Car service (major)",
            TargetAmount = 2000m,
            TargetDate = goal.TargetDate,
            Priority = "High"
        });

        var stored = context.SavingsGoals.Single();
        Assert.Equal(900m, stored.EarmarkedAmount);
        Assert.Equal(2000m, stored.TargetAmount);
        Assert.Equal("High", stored.Priority);
    }

    [Fact]
    public async Task DeleteGoalAsync_ReleasesTheEarmarkBackToTheFreeRemainder()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 900m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();
        var service = NewService(context);

        Assert.Equal(SavingsGoalMutationStatus.Success, await service.DeleteGoalAsync(goal.Id));

        var summary = await service.GetPoolSummaryAsync();
        Assert.Equal(0m, summary.TotalEarmarked);
        Assert.Equal(3000m, summary.Unassigned);
    }

    [Fact]
    public async Task FundCurrentCycleAsync_DistributesTheFreeRemainderAtEachGoalsPace()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var car = NewGoal("Car service", 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));
        var house = NewGoal("House deposit", 60000m, earmarked: 2000m, targetDate: new DateOnly(2032, 7, 1));
        context.SavingsGoals.AddRange(car, house);
        await context.SaveChangesAsync();

        // 600 unassigned. Car needs 266.67, house wants the rest and is capped by what is left.
        var result = await NewService(context).FundCurrentCycleAsync();

        Assert.Equal(SavingsGoalMutationStatus.Success, result.Status);
        Assert.Equal(600m, result.TotalGranted);
        Assert.Equal(0m, result.FreeToSpend);
        Assert.Equal(666.67m, context.SavingsGoals.Single(goal => goal.Id == car.Id).EarmarkedAmount);
        Assert.Equal(2333.33m, context.SavingsGoals.Single(goal => goal.Id == house.Id).EarmarkedAmount);
    }

    [Fact]
    public async Task FundCurrentCycleAsync_NeverPushesTotalEarmarksPastTheRewardsBalance()
    {
        await using var context = NewContext(rewardsBalance: 1000m);
        context.SavingsGoals.Add(NewGoal("Car service", 5000m, earmarked: 0m, targetDate: new DateOnly(2026, 8, 20)));
        context.SavingsGoals.Add(NewGoal("House deposit", 60000m, earmarked: 0m, targetDate: new DateOnly(2032, 7, 1)));
        await context.SaveChangesAsync();
        var service = NewService(context);

        await service.FundCurrentCycleAsync();

        var summary = await service.GetPoolSummaryAsync();
        Assert.Equal(1000m, summary.TotalEarmarked);
        Assert.Equal(0m, summary.Unassigned);
        Assert.True(summary.TotalEarmarked <= summary.RewardsBalance);
    }

    [Fact]
    public async Task FundCurrentCycleAsync_IsIdempotentWithinTheSameCycle()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();
        var service = NewService(context);

        await service.FundCurrentCycleAsync();
        var afterFirst = context.SavingsGoals.Single().EarmarkedAmount;
        var second = await service.FundCurrentCycleAsync();

        // A second tap in the same cycle must not double-contribute.
        Assert.Equal(0m, second.TotalGranted);
        Assert.Equal(afterFirst, context.SavingsGoals.Single().EarmarkedAmount);
        Assert.Equal("2026-07", context.SavingsGoals.Single().LastFundedCycleKey);
    }

    [Fact]
    public async Task FundCurrentCycleAsync_StampsTheCycleEvenWhenThereIsNothingToGive()
    {
        await using var context = NewContext(rewardsBalance: 0m);
        context.SavingsGoals.Add(NewGoal("Car service", 1200m, earmarked: 0m, targetDate: new DateOnly(2026, 9, 20)));
        await context.SaveChangesAsync();

        var result = await NewService(context).FundCurrentCycleAsync();

        Assert.Equal(0m, result.TotalGranted);
        // Without the stamp the waterfall would re-run on every page load for no benefit.
        Assert.Equal("2026-07", context.SavingsGoals.Single().LastFundedCycleKey);
    }

    [Fact]
    public async Task CompleteGoalAsync_ReleasesTheEarmarkWithoutTouchingTheLedger()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 1200m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();
        var transactionsBefore = context.Transactions.Count();

        var result = await NewService(context).CompleteGoalAsync(goal.Id);

        Assert.Equal(SavingsGoalMutationStatus.Success, result.Status);
        var stored = context.SavingsGoals.Single();
        Assert.Equal(SavingsGoalStatus.Completed, stored.Status);
        Assert.Equal(0m, stored.EarmarkedAmount);
        Assert.NotNull(stored.CompletedAt);
        // The real outgoing is an ordinary Rewards expense the user logs; completing a goal is
        // pure bookkeeping and must not synthesise a ledger row.
        Assert.Equal(transactionsBefore, context.Transactions.Count());
    }

    [Fact]
    public async Task CompleteGoalAsync_RollsARecurringGoalForwardInsteadOfClosingIt()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 1200m, targetDate: new DateOnly(2026, 9, 20));
        goal.IsRecurring = true;
        goal.RecurrenceMonths = 3;
        goal.LastFundedCycleKey = "2026-07";
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        await NewService(context).CompleteGoalAsync(goal.Id);

        var stored = context.SavingsGoals.Single();
        Assert.Equal(SavingsGoalStatus.Active, stored.Status);
        Assert.Equal(0m, stored.EarmarkedAmount);
        // Rolled from the deadline that just passed, so quarterly stays on its quarter boundaries.
        Assert.Equal(new DateTime(2026, 12, 20), stored.TargetDate.Date);
        // Cleared so the new period can be funded immediately rather than waiting a cycle.
        Assert.Null(stored.LastFundedCycleKey);
    }

    [Fact]
    public async Task CompleteGoalAsync_RejectsAnAlreadyCompletedGoal()
    {
        await using var context = NewContext(rewardsBalance: 1000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 0m, targetDate: new DateOnly(2026, 9, 20));
        goal.Status = SavingsGoalStatus.Completed;
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var result = await NewService(context).CompleteGoalAsync(goal.Id);

        Assert.Equal(SavingsGoalMutationStatus.AlreadyCompleted, result.Status);
    }

    [Fact]
    public async Task GetGoalsAsync_ListsActiveGoalsInFundingOrderWithCompletedOnesLast()
    {
        await using var context = NewContext(rewardsBalance: 1000m);
        var low = NewGoal("Low", 100m, earmarked: 0m, targetDate: new DateOnly(2026, 8, 1));
        low.Priority = "Low";
        var high = NewGoal("High", 100m, earmarked: 0m, targetDate: new DateOnly(2030, 1, 1));
        high.Priority = "High";
        var done = NewGoal("Done", 100m, earmarked: 0m, targetDate: new DateOnly(2026, 6, 1));
        done.Status = SavingsGoalStatus.Completed;
        done.CompletedAt = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
        context.SavingsGoals.AddRange(low, high, done);
        await context.SaveChangesAsync();

        var goals = await NewService(context).GetGoalsAsync();

        Assert.Equal(["High", "Low", "Done"], goals.Select(goal => goal.Name).ToArray());
    }

    [Fact]
    public async Task GetRewardsBalanceAsync_CarriesTheOpeningBalanceForwardAndAddsThisCyclesMovement()
    {
        await using var context = NewContext(rewardsBalance: 0m);
        // Closing balance of the cycle before the current one (July 2026 with cycleDay 1).
        context.CycleBalances.Add(new CycleBalance
        {
            Year = 2026,
            MonthIndex = 6,
            RewardsBalance = 800m
        });
        context.Transactions.Add(new Transaction
        {
            Id = "tx-current",
            Date = new DateTime(2026, 7, 10),
            Description = "Rewards allocation",
            Category = "Other",
            LedgerCategory = "Rewards",
            Amount = 250m
        });
        await context.SaveChangesAsync();

        var balance = await NewService(context).GetRewardsBalanceAsync(cycleDay: 1);

        Assert.Equal(1050m, balance);
    }

    // Seeds the pool by crediting the current cycle's Rewards ledger, which is exactly how the real
    // balance arises -- so the invariant is exercised against the same number the dashboard shows.
    private static AppDbContext NewContext(decimal rewardsBalance)
    {
        var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        if (rewardsBalance != 0m)
        {
            context.Transactions.Add(new Transaction
            {
                Id = "tx-rewards-seed",
                Date = Today.ToDateTime(TimeOnly.MinValue),
                Description = "Rewards allocation",
                Category = "Other",
                LedgerCategory = "Rewards",
                Amount = rewardsBalance
            });
        }
        context.SaveChanges();
        return context;
    }

    private static SavingsGoalService NewService(AppDbContext context)
    {
        var configuration = TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC"));
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(Today.ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero));
        return new SavingsGoalService(
            context,
            new CycleBalanceService(context),
            new FinancialClock(configuration, timeProvider));
    }

    private static SavingsGoal NewGoal(
        string name,
        decimal target,
        DateOnly targetDate,
        decimal earmarked = 0m,
        string? clientKey = null)
    {
        return new SavingsGoal
        {
            Name = name,
            TargetAmount = target,
            EarmarkedAmount = earmarked,
            TargetDate = targetDate.ToDateTime(TimeOnly.MinValue),
            Priority = "Medium",
            ClientKey = clientKey,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
