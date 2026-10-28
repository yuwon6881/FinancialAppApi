using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.SavingsGoals;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class SavingsGoalInterestTests
{
    private static readonly DateOnly Today = new(2026, 7, 15);

    [Fact]
    public async Task RecordInterestAsync_IncreasesEarmarkAndBucketBalance_KeepingUnassignedFreeToSpendUnchanged()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Car service", 1200m, new DateOnly(2026, 9, 20), earmarked: 400m);
        goal.LastCompletionTransactionId = "prev-completion-id";
        goal.CycleFundedAmount = 100m;
        goal.CycleFundedKey = "2026-07";
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var service = NewService(context);
        var summaryBefore = await service.GetPoolSummaryAsync(SavingsGoalFundingBucket.Rewards);
        Assert.Equal(3000m, summaryBefore.RewardsBalance);
        Assert.Equal(400m, summaryBefore.TotalEarmarked);
        Assert.Equal(2600m, summaryBefore.Unassigned);

        var result = await service.RecordInterestAsync(
            goal.Id,
            50m,
            "acct-rewards",
            "op-success-1");

        Assert.Equal(SavingsGoalMutationStatus.Success, result.Status);
        Assert.NotNull(result.Goal);
        Assert.Equal(450m, result.Goal.EarmarkedAmount);
        Assert.Null(result.Goal.LastCompletionTransactionId);
        // Interest does not inflate cycle funding
        Assert.Equal(100m, result.Goal.CycleFundedAmount);
        Assert.Equal("2026-07", result.Goal.CycleFundedKey);

        var transactions = await context.Transactions
            .Where(t => t.Id == "goal-interest-op-success-1")
            .ToListAsync();
        Assert.Single(transactions);
        var tx = transactions[0];
        Assert.Equal(TransactionDate.StartOfDate(Today), tx.Date);
        Assert.Equal("Interest earned - Car service", tx.Description);
        Assert.Equal("Adjustment", tx.Category);
        Assert.Equal("Rewards", tx.LedgerCategory);
        Assert.Equal(50m, tx.Amount);
        Assert.Equal("acct-rewards", tx.AccountId);
        Assert.True(tx.IsAccountBalanceAdjustment);
        Assert.True(tx.ExcludeFromAutocomplete);
        Assert.Equal(StabilityReloadIntent.NotRequired, tx.StabilityReloadIntent);

        // Free-to-spend (unassigned) remains exactly what it was before
        var summaryAfter = await service.GetPoolSummaryAsync(SavingsGoalFundingBucket.Rewards);
        Assert.Equal(3050m, summaryAfter.RewardsBalance);
        Assert.Equal(450m, summaryAfter.TotalEarmarked);
        Assert.Equal(summaryBefore.Unassigned, summaryAfter.Unassigned);
        Assert.Equal(2600m, summaryAfter.Unassigned);
    }

    [Fact]
    public async Task RecordInterestAsync_OverTarget_RejectsAndLeavesEarmarkAndLedgerUnchanged()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Emergency stash", 500m, new DateOnly(2026, 9, 20), earmarked: 450m);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var service = NewService(context);
        var result = await service.RecordInterestAsync(
            goal.Id,
            60m,
            "acct-rewards",
            "op-over-target");

        Assert.Equal(SavingsGoalMutationStatus.Conflict, result.Status);
        Assert.Equal("Only RM 50 of room is left before the target. Raise the target first.", result.Message);

        var reloadedGoal = await context.SavingsGoals.FindAsync(goal.Id);
        Assert.Equal(450m, reloadedGoal!.EarmarkedAmount);
        Assert.Empty(await context.Transactions.Where(t => t.Id == "goal-interest-op-over-target").ToListAsync());
    }

    [Fact]
    public async Task RecordInterestAsync_WrongBucketAccount_RejectsWithoutWriting()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Rewards goal", 1000m, new DateOnly(2026, 9, 20), earmarked: 100m);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var service = NewService(context);
        var result = await service.RecordInterestAsync(
            goal.Id,
            25m,
            "acct-essentials", // Essentials account for Rewards goal
            "op-wrong-bucket");

        Assert.Equal(SavingsGoalMutationStatus.InvalidAccount, result.Status);
        Assert.Equal("Choose an account in the Rewards bucket.", result.Message);
        Assert.Empty(await context.Transactions.Where(t => t.Id == "goal-interest-op-wrong-bucket").ToListAsync());
        var reloaded = await context.SavingsGoals.FindAsync(goal.Id);
        Assert.Equal(100m, reloaded!.EarmarkedAmount);
    }

    [Fact]
    public async Task RecordInterestAsync_CreditCardAccount_RejectsWithoutWriting()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "acct-card",
            Name = "Credit Card",
            Bucket = "Rewards",
            Kind = LedgerAccountKind.CreditCard,
        });
        var goal = NewGoal("Rewards goal", 1000m, new DateOnly(2026, 9, 20), earmarked: 100m);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var service = NewService(context);
        var result = await service.RecordInterestAsync(
            goal.Id,
            25m,
            "acct-card",
            "op-card");

        Assert.Equal(SavingsGoalMutationStatus.InvalidAccount, result.Status);
        Assert.Equal("A credit card account cannot receive interest.", result.Message);
        Assert.Empty(await context.Transactions.Where(t => t.Id == "goal-interest-op-card").ToListAsync());
    }

    [Fact]
    public async Task RecordInterestAsync_InactiveAccount_RejectsWithoutWriting()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "acct-archived",
            Name = "Archived Rewards Account",
            Bucket = "Rewards",
            Kind = LedgerAccountKind.Bank,
            IsArchived = true,
        });
        var goal = NewGoal("Rewards goal", 1000m, new DateOnly(2026, 9, 20), earmarked: 100m);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var service = NewService(context);
        var result = await service.RecordInterestAsync(
            goal.Id,
            25m,
            "acct-archived",
            "op-archived");

        Assert.Equal(SavingsGoalMutationStatus.InvalidAccount, result.Status);
        Assert.Equal("Choose an account in the Rewards bucket.", result.Message);
        Assert.Empty(await context.Transactions.Where(t => t.Id == "goal-interest-op-archived").ToListAsync());
    }

    [Fact]
    public async Task RecordInterestAsync_AnotherUsersAccountOrNonExistent_RejectsWithoutWriting()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Rewards goal", 1000m, new DateOnly(2026, 9, 20), earmarked: 100m);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        // Seed an account for another user
        context.SetCurrentUser("different-user");
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "acct-other-user",
            UserId = "different-user",
            Name = "Other User Account",
            Bucket = "Rewards",
            Kind = LedgerAccountKind.Bank,
        });
        await context.SaveChangesAsync();
        context.SetCurrentUser(TestHelpers.DefaultUserId);

        var service = NewService(context);
        var otherUserResult = await service.RecordInterestAsync(
            goal.Id,
            25m,
            "acct-other-user",
            "op-other-user");

        Assert.Equal(SavingsGoalMutationStatus.InvalidAccount, otherUserResult.Status);
        Assert.Equal("Choose an account in the Rewards bucket.", otherUserResult.Message);

        var nonExistentResult = await service.RecordInterestAsync(
            goal.Id,
            25m,
            "non-existent-id",
            "op-nonexistent");

        Assert.Equal(SavingsGoalMutationStatus.InvalidAccount, nonExistentResult.Status);
        Assert.Equal("Choose an account in the Rewards bucket.", nonExistentResult.Message);
    }

    [Fact]
    public async Task RecordInterestAsync_CompletedGoal_ReturnsAlreadyCompletedWithoutWriting()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Completed goal", 1000m, new DateOnly(2026, 9, 20), earmarked: 0m);
        goal.Status = SavingsGoalStatus.Completed;
        goal.CompletedAt = DateTime.UtcNow;
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var service = NewService(context);
        var result = await service.RecordInterestAsync(
            goal.Id,
            50m,
            "acct-rewards",
            "op-completed");

        Assert.Equal(SavingsGoalMutationStatus.AlreadyCompleted, result.Status);
        Assert.Equal("This goal is already completed.", result.Message);
        Assert.Empty(await context.Transactions.Where(t => t.Id == "goal-interest-op-completed").ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task RecordInterestAsync_NonPositiveAmount_ReturnsInvalid(decimal amount)
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Active goal", 1000m, new DateOnly(2026, 9, 20), earmarked: 100m);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var service = NewService(context);
        var result = await service.RecordInterestAsync(
            goal.Id,
            amount,
            "acct-rewards",
            "op-invalid-amt");

        Assert.Equal(SavingsGoalMutationStatus.ContributionInvalid, result.Status);
        Assert.Equal(100m, (await context.SavingsGoals.FindAsync(goal.Id))!.EarmarkedAmount);
    }

    [Fact]
    public async Task RecordInterestAsync_RepeatedOperationId_SameDataIsIdempotent_DifferentAmountIsConflict()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Vacation", 1000m, new DateOnly(2026, 9, 20), earmarked: 200m);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var service = NewService(context);
        var first = await service.RecordInterestAsync(
            goal.Id,
            50m,
            "acct-rewards",
            "idemp-1");

        Assert.Equal(SavingsGoalMutationStatus.Success, first.Status);
        Assert.Equal(250m, first.Goal!.EarmarkedAmount);
        Assert.Single(await context.Transactions.Where(t => t.Id == "goal-interest-idemp-1").ToListAsync());

        // Repeated operation with identical data: success, earmark NOT increased twice
        var repeated = await service.RecordInterestAsync(
            goal.Id,
            50m,
            "acct-rewards",
            "idemp-1");

        Assert.Equal(SavingsGoalMutationStatus.Success, repeated.Status);
        Assert.Equal(250m, repeated.Goal!.EarmarkedAmount);
        Assert.Single(await context.Transactions.Where(t => t.Id == "goal-interest-idemp-1").ToListAsync());

        // Same operation ID with differing amount: conflict
        var conflicting = await service.RecordInterestAsync(
            goal.Id,
            75m,
            "acct-rewards",
            "idemp-1");

        Assert.Equal(SavingsGoalMutationStatus.Conflict, conflicting.Status);
        Assert.Equal(250m, (await context.SavingsGoals.FindAsync(goal.Id))!.EarmarkedAmount);
        Assert.Single(await context.Transactions.Where(t => t.Id == "goal-interest-idemp-1").ToListAsync());
    }

    [Fact]
    public async Task RecordInterestAsync_InterestTransaction_ExcludedFromReportableCashMovementAndInflow()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Laptop", 2000m, new DateOnly(2026, 9, 20), earmarked: 500m);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var service = NewService(context);
        var result = await service.RecordInterestAsync(
            goal.Id,
            30m,
            "acct-rewards",
            "op-reporting-test");

        Assert.Equal(SavingsGoalMutationStatus.Success, result.Status);
        var tx = await context.Transactions.SingleAsync(t => t.Id == "goal-interest-op-reporting-test");

        // Verify reporting exclusion semantics
        Assert.True(TransactionReportSemantics.IsBalanceAdjustment(tx));
        Assert.False(TransactionReportSemantics.IsReportableCashMovement(tx));
        Assert.False(TransactionReportSemantics.IsReportableInflow(tx));
        Assert.False(TransactionReportSemantics.IsReportableIncome(tx));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has whitespace")]
    [InlineData("has\ttab")]
    public async Task RecordInterestAsync_InvalidOperationId_RejectsWithoutWriting(string operationId)
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Active goal", 1000m, new DateOnly(2026, 9, 20), earmarked: 100m);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var service = NewService(context);
        var result = await service.RecordInterestAsync(
            goal.Id,
            25m,
            "acct-rewards",
            operationId);

        Assert.NotEqual(SavingsGoalMutationStatus.Success, result.Status);
        Assert.Empty(await context.Transactions.Where(t => t.Id.StartsWith("goal-interest-")).ToListAsync());
    }

    [Fact]
    public async Task RecordInterestAsync_EssentialsBucketGoal_SucceedsAndPreservesEssentialsUnassigned()
    {
        await using var context = NewContext(rewardsBalance: 0m);
        context.Transactions.Add(new Transaction
        {
            Id = "tx-essentials-seed",
            Date = Today.ToDateTime(TimeOnly.MinValue),
            Description = "Essentials seed",
            Category = "Other",
            LedgerCategory = "Essentials",
            Amount = 1500m,
            AccountId = "acct-essentials",
        });
        var goal = NewGoal(
            "Car repair fund",
            1000m,
            new DateOnly(2026, 9, 20),
            earmarked: 300m,
            fundingBucket: SavingsGoalFundingBucket.Essentials);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var service = NewService(context);
        var summaryBefore = await service.GetPoolSummaryAsync(SavingsGoalFundingBucket.Essentials);
        Assert.Equal(1500m, summaryBefore.Balance);
        Assert.Equal(300m, summaryBefore.TotalEarmarked);
        Assert.Equal(1200m, summaryBefore.Unassigned);

        var result = await service.RecordInterestAsync(
            goal.Id,
            40m,
            "acct-essentials",
            "op-essentials-1");

        Assert.Equal(SavingsGoalMutationStatus.Success, result.Status);
        Assert.Equal(340m, result.Goal!.EarmarkedAmount);

        var summaryAfter = await service.GetPoolSummaryAsync(SavingsGoalFundingBucket.Essentials);
        Assert.Equal(1540m, summaryAfter.Balance);
        Assert.Equal(340m, summaryAfter.TotalEarmarked);
        Assert.Equal(summaryBefore.Unassigned, summaryAfter.Unassigned);
        Assert.Equal(1200m, summaryAfter.Unassigned);
    }

    [Fact]
    public async Task RecordInterestAsync_AcquiresSharedPoolMutationLock()
    {
        await using var context = NewContext(rewardsBalance: 3000m);
        var goal = NewGoal("Locked goal", 1000m, new DateOnly(2026, 9, 20), earmarked: 100m);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var countingLock = new CountingMutationLock();
        var configuration = TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC"));
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(Today.ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero));
        var service = new SavingsGoalService(
            context,
            new CycleBalanceService(context),
            new FinancialClock(configuration, timeProvider),
            sharedPoolMutationLock: countingLock);

        var result = await service.RecordInterestAsync(
            goal.Id,
            25m,
            "acct-rewards",
            "op-lock-test");

        Assert.True(countingLock.AcquisitionCount > 0);
    }

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

    private sealed class CountingMutationLock : ISharedPoolMutationLock
    {
        public int AcquisitionCount { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            AcquisitionCount++;
            return Task.FromResult<IAsyncDisposable>(NoOpLease.Instance);
        }
    }

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static readonly NoOpLease Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
