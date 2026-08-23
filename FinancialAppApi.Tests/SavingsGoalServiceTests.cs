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
    public async Task GetPoolSummaryAsync_SeparatesEssentialsFromRewards()
    {
        await using var context = NewContext(rewardsBalance: 0m);
        context.Transactions.Add(new Transaction
        {
            Id = "tx-essentials-seed",
            Date = Today.ToDateTime(TimeOnly.MinValue),
            Description = "Essentials allocation",
            Category = "Other",
            LedgerCategory = "Essentials",
            Amount = 1000m,
            AccountId = "acct-essentials",
        });
        context.SavingsGoals.Add(NewGoal("Home reserve", 500m, new DateOnly(2026, 9, 20), earmarked: 300m, fundingBucket: SavingsGoalFundingBucket.Essentials));
        context.SavingsGoals.Add(NewGoal("Reward", 500m, new DateOnly(2026, 9, 20), earmarked: 200m));
        await context.SaveChangesAsync();

        var summary = await NewService(context).GetPoolSummaryAsync(SavingsGoalFundingBucket.Essentials);

        Assert.Equal(SavingsGoalFundingBucket.Essentials, summary.FundingBucket);
        Assert.Equal(1000m, summary.RewardsBalance);
        Assert.Equal(300m, summary.TotalEarmarked);
        Assert.Equal(700m, summary.Unassigned);
    }

    [Fact]
    public async Task CreateGoalAsync_RejectsGrowthAndStabilityFundingBuckets()
    {
        await using var context = NewContext(rewardsBalance: 1000m);
        var service = NewService(context);

        var growth = await service.CreateGoalAsync(NewGoal("Growth", 100m, new DateOnly(2026, 9, 20), fundingBucket: "Growth"));
        var stability = await service.CreateGoalAsync(NewGoal("Stability", 100m, new DateOnly(2026, 9, 20), fundingBucket: "Stability"));

        Assert.Equal(SavingsGoalMutationStatus.FundingBucketInvalid, growth.Status);
        Assert.Equal(SavingsGoalMutationStatus.FundingBucketInvalid, stability.Status);
    }

    [Fact]
    public async Task GetPoolSummaryAsync_HoldsPendingRewardsOccurrenceOutOfFreeBalance()
    {
        await using var context = NewContext(rewardsBalance: 1150m);
        context.SavingsGoals.Add(NewGoal(
            "Camera commitment",
            300m,
            targetDate: new DateOnly(2026, 9, 20),
            earmarked: 100m));
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "chatgpt-plus",
            Name = "ChatGPT Plus",
            Amount = 150m,
            Frequency = "Monthly",
            Category = "Entertainment",
            LedgerCategory = "Rewards",
            AccountId = "acct-rewards",
            StartDate = "2026-01-27",
            NextDueDate = "2026-07-27",
            DueDate = 27,
            Active = true
        });
        // A transaction for the next occurrence must not settle this month's bill merely because
        // it was posted in the current cycle.
        context.Transactions.Add(new Transaction
        {
            Id = "chatgpt-next-month",
            Date = new DateTime(2026, 7, 5),
            Description = "ChatGPT Plus",
            Category = "Entertainment",
            LedgerCategory = "Rewards",
            Amount = -150m,
            AccountId = "acct-rewards",
            RecurringPaymentId = "chatgpt-plus",
            RecurringOccurrenceDate = new DateOnly(2026, 8, 27)
        });
        await context.SaveChangesAsync();

        var summary = await NewService(context).GetPoolSummaryAsync();

        Assert.Equal(850m, summary.RewardsBalance);
        Assert.Equal(100m, summary.TotalEarmarked);
        Assert.Equal(750m, summary.Unassigned);
    }

    [Fact]
    public async Task GetPoolSummaryAsync_DoesNotHoldPausedPaymentOccurrenceOutOfFreeBalance()
    {
        await using var context = NewContext(rewardsBalance: 1000m);
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "paused-reward",
            Name = "Paused reward bill",
            Amount = -250m,
            Frequency = "Monthly",
            Category = "Subscriptions",
            LedgerCategory = "Rewards",
            AccountId = "acct-rewards",
            StartDate = "2026-07-20",
            NextDueDate = "2026-07-20",
            DueDate = 20,
            Active = false
        });
        context.RecurringPaymentOccurrences.Add(new RecurringPaymentOccurrence
        {
            Id = "occ-paused-reward-20260720",
            RecurringPaymentId = "paused-reward",
            OccurrenceDate = new DateOnly(2026, 7, 20),
            Name = "Paused reward bill",
            ScheduledAmount = 250m,
            Category = "Subscriptions",
            LedgerCategory = "Rewards",
            Status = RecurringOccurrenceStatus.Pending,
            UserId = TestHelpers.DefaultUserId
        });
        await context.SaveChangesAsync();

        var summary = await NewService(context).GetPoolSummaryAsync();

        Assert.Equal(1000m, summary.Unassigned);
    }

    // The money for a legacy bill has already left the Rewards balance, so holding the same
    // amount back a second time as "still pending" would understate free Rewards by the bill
    // twice over. Untagged pre-ledger history is kept out of that sum by
    // OccurrenceTrackingStartDate -- the occurrence is never materialised, so it can never be
    // counted -- which is the guarantee the old posting-date fallback used to provide.
    [Fact]
    public async Task GetPoolSummaryAsync_DoesNotHoldBackBillsSettledBeforeTrackingStarted()
    {
        await using var context = NewContext(rewardsBalance: 1150m);
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "legacy-rewards-bill",
            Name = "Legacy Rewards bill",
            Amount = 150m,
            Frequency = "Monthly",
            Category = "Entertainment",
            LedgerCategory = "Rewards",
            AccountId = "acct-rewards",
            StartDate = "2026-01-27",
            NextDueDate = "2026-07-27",
            DueDate = 27,
            OccurrenceTrackingStartDate = new DateOnly(2026, 8, 1),
            Active = true
        });
        context.Transactions.Add(new Transaction
        {
            Id = "legacy-rewards-payment",
            Date = new DateTime(2026, 7, 5),
            Description = "Legacy Rewards bill",
            Category = "Entertainment",
            LedgerCategory = "Rewards",
            Amount = -150m,
            AccountId = "acct-rewards",
            RecurringPaymentId = "legacy-rewards-bill"
        });
        await context.SaveChangesAsync();

        var summary = await NewService(context).GetPoolSummaryAsync();

        Assert.Equal(1000m, summary.Unassigned);
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
        await using var context = NewContext(rewardsBalance: 3000m);
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
    public async Task UpdateGoalAsync_TakesReleasedMoneyOffThisCyclesTally()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 900m, targetDate: new DateOnly(2026, 9, 20));
        // All 900 went in during the current cycle, so the goal currently owes nothing more.
        goal.CycleFundedKey = "2026-07";
        goal.CycleFundedAmount = 900m;
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var service = NewService(context);
        await service.UpdateGoalAsync(goal.Id, new SavingsGoal
        {
            Id = goal.Id,
            Name = "Car service",
            TargetAmount = 100m,
            TargetDate = goal.TargetDate,
            Priority = "Medium"
        });

        // Releasing 800 has to come off this cycle's tally too, exactly as a manual release does.
        var stored = context.SavingsGoals.Single();
        Assert.Equal(100m, stored.EarmarkedAmount);
        Assert.Equal(100m, stored.CycleFundedAmount);
        Assert.Equal("2026-07", stored.CycleFundedKey);

        // And the tally has to be right for the *next* mutation: raising the target again must
        // reopen funding. With a stale 900 on the clock the goal reports nothing outstanding and
        // "Fund this cycle" silently skips it.
        await service.UpdateGoalAsync(goal.Id, new SavingsGoal
        {
            Id = goal.Id,
            Name = "Car service",
            TargetAmount = 2000m,
            TargetDate = goal.TargetDate,
            Priority = "Medium"
        });

        var summary = await service.GetPoolSummaryAsync();
        Assert.True(summary.OutstandingThisCycleTotal > 0m);
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
    public async Task FundCurrentCycleAsync_FundsOnlyTheRequestedBucket()
    {
        await using var context = NewContext(rewardsBalance: 1000m);
        context.Transactions.Add(new Transaction
        {
            Id = "tx-essentials-seed",
            Date = Today.ToDateTime(TimeOnly.MinValue),
            Description = "Essentials allocation",
            Category = "Other",
            LedgerCategory = SavingsGoalFundingBucket.Essentials,
            Amount = 1000m,
            AccountId = "acct-essentials"
        });
        var rewardsGoal = NewGoal("Reward", 500m, new DateOnly(2026, 9, 20));
        var essentialsGoal = NewGoal(
            "Home reserve",
            500m,
            new DateOnly(2026, 9, 20),
            fundingBucket: SavingsGoalFundingBucket.Essentials);
        context.SavingsGoals.AddRange(rewardsGoal, essentialsGoal);
        await context.SaveChangesAsync();

        await NewService(context).FundCurrentCycleAsync(SavingsGoalFundingBucket.Essentials);

        Assert.Equal(0m, context.SavingsGoals.Single(goal => goal.Id == rewardsGoal.Id).EarmarkedAmount);
        Assert.True(context.SavingsGoals.Single(goal => goal.Id == essentialsGoal.Id).EarmarkedAmount > 0m);
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
        Assert.Equal("2026-07", context.SavingsGoals.Single().CycleFundedKey);
    }

    [Fact]
    public async Task FundCurrentCycleAsync_RefundsOnlyWhatWasReleasedAfterFunding()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();
        var service = NewService(context);

        await service.FundCurrentCycleAsync();
        Assert.Equal(666.67m, context.SavingsGoals.Single().EarmarkedAmount);

        // Release 100 of what was just set aside, then fund again.
        await service.ContributeAsync(goal.Id, -100m);
        var second = await service.FundCurrentCycleAsync();

        // Exactly the released 100 comes back -- not another full per-cycle contribution.
        Assert.Equal(100m, second.TotalGranted);
        Assert.Equal(666.67m, context.SavingsGoals.Single().EarmarkedAmount);
    }

    [Fact]
    public async Task FundCurrentCycleAsync_SkipsAGoalTheUserAlreadyToppedUpByHand()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var manual = NewGoal("Car service", 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));
        var untouched = NewGoal("Laptop fund", 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.AddRange(manual, untouched);
        await context.SaveChangesAsync();
        var service = NewService(context);

        // Cover this cycle's pace by hand first.
        await service.ContributeAsync(manual.Id, 266.67m);
        var result = await service.FundCurrentCycleAsync();

        // Only the goal that had nothing this cycle receives anything.
        Assert.Equal(266.67m, result.TotalGranted);
        Assert.Equal(666.67m, context.SavingsGoals.Single(goal => goal.Id == manual.Id).EarmarkedAmount);
        Assert.Equal(666.67m, context.SavingsGoals.Single(goal => goal.Id == untouched.Id).EarmarkedAmount);
    }

    [Fact]
    public async Task FundCurrentCycleAsync_LeavesTheTallyAloneWhenThereIsNothingToGive()
    {
        await using var context = NewContext(rewardsBalance: 0m);
        context.SavingsGoals.Add(NewGoal("Car service", 1200m, earmarked: 0m, targetDate: new DateOnly(2026, 9, 20)));
        await context.SaveChangesAsync();

        var result = await NewService(context).FundCurrentCycleAsync();

        // Nothing was contributed, so nothing is recorded against the cycle -- the goal stays
        // fundable the moment money arrives, rather than being latched shut for the cycle.
        Assert.Equal(0m, result.TotalGranted);
        Assert.Null(context.SavingsGoals.Single().CycleFundedKey);
        Assert.Equal(0m, context.SavingsGoals.Single().CycleFundedAmount);
    }

    [Fact]
    public async Task GetPoolSummaryAsync_ReportsNothingOutstandingOnceEveryGoalIsPaced()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        context.SavingsGoals.Add(NewGoal("Car service", 1200m, earmarked: 400m, targetDate: new DateOnly(2026, 9, 20)));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var before = await service.GetPoolSummaryAsync();
        Assert.Equal(266.67m, before.OutstandingThisCycleTotal);

        await service.FundCurrentCycleAsync();

        var after = await service.GetPoolSummaryAsync();
        // This is what the UI keys the funding action off, so it must go to zero once paced.
        Assert.Equal(0m, after.OutstandingThisCycleTotal);
        // The per-cycle requirement itself is unchanged -- only the outstanding part moved.
        Assert.True(after.RequiredPerCycleTotal > 0m);
    }

    [Fact]
    public async Task ContributeAsync_DoesNotLetAReleaseOfOlderMoneyInflateThisCycle()
    {
        // Earmarked in an earlier cycle: no tally for the current one.
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 800m, targetDate: new DateOnly(2026, 9, 20));
        goal.CycleFundedKey = "2026-06";
        goal.CycleFundedAmount = 800m;
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();
        var service = NewService(context);

        await service.ContributeAsync(goal.Id, -300m);

        // The tally floors at zero rather than going negative, so the goal cannot claim more than
        // one cycle's pace when funding next runs. That pace is re-derived from the new position
        // (700 still needed over 3 cycles), not from the pre-release one.
        Assert.Equal(0m, context.SavingsGoals.Single().CycleFundedAmount);
        var summary = await service.GetPoolSummaryAsync();
        Assert.Equal(233.34m, summary.OutstandingThisCycleTotal);
    }

    [Fact]
    public async Task CompleteGoalAsync_SpendsTheEarmarkWithoutIncreasingFreeRewards()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 1200m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();
        var freeBefore = (await NewService(context).GetPoolSummaryAsync()).Unassigned;

        var result = await NewService(context).CompleteGoalAsync(goal.Id, "acct-rewards");

        Assert.Equal(SavingsGoalMutationStatus.Success, result.Status);
        var stored = context.SavingsGoals.Single();
        Assert.Equal(SavingsGoalStatus.Completed, stored.Status);
        Assert.Equal(0m, stored.EarmarkedAmount);
        Assert.NotNull(stored.CompletedAt);
        var transaction = Assert.Single(context.Transactions.Where(item => item.SavingsGoalId == goal.Id));
        Assert.Equal(-1200m, transaction.Amount);
        Assert.Equal("Rewards", transaction.LedgerCategory);
        Assert.Equal(goal.Id, transaction.SavingsGoalId);
        Assert.Equal(transaction.Id, stored.LastCompletionTransactionId);
        Assert.Single(context.SavingsGoalCompletions);
        // Balance and earmark fall together, so Done cannot turn already-spoken-for money into free cash.
        Assert.Equal(freeBefore, (await NewService(context).GetPoolSummaryAsync()).Unassigned);
    }

    [Fact]
    public async Task CompleteGoalAsync_RollsARecurringGoalForwardInsteadOfClosingIt()
    {
        await using var context = NewContext(rewardsBalance: 5000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 1200m, targetDate: new DateOnly(2026, 9, 20));
        goal.IsRecurring = true;
        goal.RecurrenceMonths = 3;
        goal.CycleFundedKey = "2026-07";
        goal.CycleFundedAmount = 400m;
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        await NewService(context).CompleteGoalAsync(goal.Id, "acct-rewards");

        var stored = context.SavingsGoals.Single();
        Assert.Equal(SavingsGoalStatus.Active, stored.Status);
        Assert.Equal(0m, stored.EarmarkedAmount);
        // Rolled from the deadline that just passed, so quarterly stays on its quarter boundaries.
        Assert.Equal(new DateTime(2026, 12, 20), stored.TargetDate.Date);
        // Cleared so the new period can be funded immediately rather than waiting a cycle.
        Assert.Null(stored.CycleFundedKey);
        Assert.Equal(0m, stored.CycleFundedAmount);
    }

    [Fact]
    public async Task CompleteGoalAsync_PreservesTheOriginalDayAcrossShortMonths()
    {
        await using var context = NewContext(rewardsBalance: 5000m);
        var goal = NewGoal("Month end service", 1200m, earmarked: 1200m, targetDate: new DateOnly(2026, 7, 31));
        goal.IsRecurring = true;
        goal.RecurrenceMonths = 1;
        var service = NewService(context, new DateOnly(2026, 7, 15));
        var created = await service.CreateGoalAsync(goal);
        Assert.Equal(SavingsGoalMutationStatus.Success, created.Status);
        Assert.Equal(31, created.Goal!.RecurrenceDayOfMonth);

        await service.CompleteGoalAsync(goal.Id, "acct-rewards");
        Assert.Equal(new DateTime(2026, 8, 31), context.SavingsGoals.Single().TargetDate.Date);
        Assert.Equal(31, context.SavingsGoals.Single().RecurrenceDayOfMonth);

        await service.ContributeAsync(goal.Id, 1200m);
        await NewService(context, new DateOnly(2026, 9, 1)).CompleteGoalAsync(goal.Id, "acct-rewards");
        Assert.Equal(new DateTime(2026, 9, 30), context.SavingsGoals.Single().TargetDate.Date);

        await NewService(context, new DateOnly(2026, 10, 1)).ContributeAsync(goal.Id, 1200m);
        await NewService(context, new DateOnly(2026, 10, 1)).CompleteGoalAsync(goal.Id, "acct-rewards");
        // The September clamp does not permanently turn the 31st into the 30th.
        Assert.Equal(new DateTime(2026, 10, 31), context.SavingsGoals.Single().TargetDate.Date);
    }

    [Fact]
    public async Task CompleteGoalAsync_SkipsAllMissedRecurringPeriods()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Overdue quarterly service", 1200m, earmarked: 1200m, targetDate: new DateOnly(2026, 1, 15));
        goal.IsRecurring = true;
        goal.RecurrenceMonths = 3;
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        await NewService(context, new DateOnly(2026, 7, 15)).CompleteGoalAsync(goal.Id, "acct-rewards");

        // Jan -> Apr -> Jul are already missed (including today's deadline), so one completion
        // advances the active goal to the next actionable quarter.
        Assert.Equal(new DateTime(2026, 10, 15), context.SavingsGoals.Single().TargetDate.Date);
    }

    [Fact]
    public async Task CompleteGoalAsync_RejectsAnAlreadyCompletedGoal()
    {
        await using var context = NewContext(rewardsBalance: 1000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 0m, targetDate: new DateOnly(2026, 9, 20));
        goal.Status = SavingsGoalStatus.Completed;
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var result = await NewService(context).CompleteGoalAsync(goal.Id, "acct-rewards");

        Assert.Equal(SavingsGoalMutationStatus.AlreadyCompleted, result.Status);
    }

    [Fact]
    public async Task CompleteGoalAsync_RejectsAGoalWithNothingSetAside()
    {
        await using var context = NewContext(rewardsBalance: 1000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 0m, targetDate: new DateOnly(2026, 9, 20));
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var result = await NewService(context).CompleteGoalAsync(goal.Id, "acct-rewards");

        Assert.Equal(SavingsGoalMutationStatus.NothingEarmarked, result.Status);
        Assert.DoesNotContain(context.Transactions, transaction => transaction.SavingsGoalId == goal.Id);
    }

    [Fact]
    public async Task DeletingACompletionTransaction_RestoresTheRecurringGoalAndItsDate()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 1200m, targetDate: new DateOnly(2026, 9, 20));
        goal.IsRecurring = true;
        goal.RecurrenceMonths = 3;
        goal.CycleFundedKey = "2026-07";
        goal.CycleFundedAmount = 400m;
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var completed = await NewService(context).CompleteGoalAsync(goal.Id, "acct-rewards");
        var transactionId = completed.CompletionTransaction!.Id;
        var occurrenceService = new RecurringOccurrenceService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RecurringOccurrenceService>.Instance);
        var cycleBalanceService = new CycleBalanceService(context);
        var persistence = new TransactionPersistenceService(
            context,
            cycleBalanceService,
            occurrenceService,
            new Services.Stability.StabilityRecoveryService(context, cycleBalanceService));

        var deleted = await persistence.DeleteTransactionAsync(transactionId);

        Assert.Equal(TransactionMutationStatus.Deleted, deleted.Status);
        var restored = context.SavingsGoals.Single();
        Assert.Equal(SavingsGoalStatus.Active, restored.Status);
        Assert.Equal(new DateTime(2026, 9, 20), restored.TargetDate.Date);
        Assert.Equal(1200m, restored.EarmarkedAmount);
        Assert.Equal("2026-07", restored.CycleFundedKey);
        Assert.Equal(400m, restored.CycleFundedAmount);
        Assert.Null(restored.LastCompletionTransactionId);
        Assert.DoesNotContain(context.Transactions, transaction => transaction.Id == transactionId);
        var reversal = Assert.Single(context.SavingsGoalCompletions);
        Assert.NotNull(reversal.ReversedAt);
        Assert.Equal(transactionId, reversal.TransactionId);
    }

    [Fact]
    public async Task CompleteGoalAsync_ReplayedWithSameTransactionId_ReturnsOriginalSettlement()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, new DateOnly(2026, 9, 20), earmarked: 1200m);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();
        var service = NewService(context);
        const string transactionId = "tx-stable-completion";
        var postedAt = new DateTime(2026, 8, 22, 4, 0, 0, DateTimeKind.Utc);

        var first = await service.CompleteGoalAsync(goal.Id, "acct-rewards", transactionId, postedAt, default);
        var replay = await service.CompleteGoalAsync(goal.Id, "acct-rewards", transactionId, postedAt, default);

        Assert.Equal(SavingsGoalMutationStatus.Success, first.Status);
        Assert.Equal(SavingsGoalMutationStatus.Success, replay.Status);
        Assert.Equal(transactionId, replay.CompletionTransaction!.Id);
        Assert.Single(context.Transactions, transaction => transaction.Id == transactionId);
        Assert.Single(context.SavingsGoalCompletions, completion => completion.TransactionId == transactionId);
    }

    [Fact]
    public async Task RestoreDeletedGoalAsync_PreservesEarmarkAndCycleFundingAndDedupesReplay()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var snapshot = NewGoal("Car service", 1200m, new DateOnly(2026, 9, 20), earmarked: 700m);
        snapshot.ClientKey = "undo-delete-goal-7";
        snapshot.CycleFundedKey = "2026-08";
        snapshot.CycleFundedAmount = 400m;
        snapshot.CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var service = NewService(context);

        var restored = await service.RestoreDeletedGoalAsync(snapshot);
        var replay = await service.RestoreDeletedGoalAsync(new SavingsGoal
        {
            ClientKey = snapshot.ClientKey,
            Name = snapshot.Name,
            TargetAmount = snapshot.TargetAmount,
            EarmarkedAmount = snapshot.EarmarkedAmount,
            TargetDate = snapshot.TargetDate,
            Priority = snapshot.Priority,
            FundingBucket = snapshot.FundingBucket,
        });

        Assert.Equal(SavingsGoalMutationStatus.Success, restored.Status);
        Assert.Equal(restored.Goal!.Id, replay.Goal!.Id);
        Assert.Equal(700m, restored.Goal.EarmarkedAmount);
        Assert.Equal("2026-08", restored.Goal.CycleFundedKey);
        Assert.Equal(400m, restored.Goal.CycleFundedAmount);
        Assert.Single(context.SavingsGoals);
    }

    [Fact]
    public async Task DeletingACompletionTransaction_RejectsOverwritingNewerGoalFunding()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, earmarked: 1200m, targetDate: new DateOnly(2026, 9, 20));
        goal.IsRecurring = true;
        goal.RecurrenceMonths = 3;
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();
        var service = NewService(context);
        var completed = await service.CompleteGoalAsync(goal.Id, "acct-rewards");
        await service.ContributeAsync(goal.Id, 100m);
        var occurrenceService = new RecurringOccurrenceService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RecurringOccurrenceService>.Instance);
        var cycleBalanceService = new CycleBalanceService(context);
        var persistence = new TransactionPersistenceService(
            context,
            cycleBalanceService,
            occurrenceService,
            new Services.Stability.StabilityRecoveryService(context, cycleBalanceService));

        var deleted = await persistence.DeleteTransactionAsync(completed.CompletionTransaction!.Id);

        Assert.Equal(TransactionMutationStatus.Conflict, deleted.Status);
        Assert.Contains("can no longer restore", deleted.Message);
        Assert.Contains(context.Transactions, transaction => transaction.Id == completed.CompletionTransaction.Id);
        Assert.Equal(100m, context.SavingsGoals.Single().EarmarkedAmount);
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
    public async Task GetBucketBalanceAsync_CarriesTheOpeningBalanceForwardAndAddsThisCyclesMovement()
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
            Amount = 250m,
            AccountId = "acct-rewards"
        });
        await context.SaveChangesAsync();

        var balance = await NewService(context).GetBucketBalanceAsync(SavingsGoalFundingBucket.Rewards, cycleDay: 1);

        Assert.Equal(1050m, balance);
    }

    // Seeds the pool by crediting the current cycle's Rewards ledger, which is exactly how the real
    // balance arises -- so the invariant is exercised against the same number the dashboard shows.
    private static AppDbContext NewContext(decimal rewardsBalance)
    {
        var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "acct-rewards",
            Name = "Rewards account",
            Bucket = "Rewards",
            Kind = LedgerAccountKind.EWallet,
        });
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "acct-essentials",
            Name = "Essentials account",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
        });
        if (rewardsBalance != 0m)
        {
            context.Transactions.Add(new Transaction
            {
                Id = "tx-rewards-seed",
                Date = Today.ToDateTime(TimeOnly.MinValue),
                Description = "Rewards allocation",
                Category = "Other",
                LedgerCategory = "Rewards",
                Amount = rewardsBalance,
                AccountId = "acct-rewards",
            });
        }
        context.SaveChanges();
        return context;
    }

    private static SavingsGoalService NewService(AppDbContext context, DateOnly? today = null)
    {
        var configuration = TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC"));
        var clockDate = today ?? Today;
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(clockDate.ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero));
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
        string? clientKey = null,
        string fundingBucket = SavingsGoalFundingBucket.Rewards)
    {
        return new SavingsGoal
        {
            Name = name,
            TargetAmount = target,
            EarmarkedAmount = earmarked,
            TargetDate = targetDate.ToDateTime(TimeOnly.MinValue),
            Priority = "Medium",
            ClientKey = clientKey,
            FundingBucket = fundingBucket,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
