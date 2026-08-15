using System.Collections.Concurrent;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.SavingsGoals;

namespace FinancialAppApi.Tests;

public sealed class SharedPoolMutationLockTests
{
    [Fact]
    public async Task AcquireAsync_UsesDisposableNoOpLeaseForNonNpgsqlProvider()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var mutationLock = new SharedPoolMutationLock(context);

        await using var lease = await mutationLock.AcquireAsync();

        Assert.Equal("NoOpLease", lease.GetType().Name);
    }

    [Fact]
    public async Task PoolMutations_AllAcquireTheSharedLease()
    {
        await using var context = NewPoolContext();
        var mutationLock = new CountingMutationLock();
        var clock = new FinancialClock(TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")));
        var savingsGoals = new SavingsGoalService(
            context,
            new CycleBalanceService(context),
            clock,
            sharedPoolMutationLock: mutationLock);
        var wishlist = new WishlistService(
            context,
            new CycleBalanceService(context),
            savingsGoals,
            clock,
            mutationLock);

        var temporary = await savingsGoals.CreateGoalAsync(NewGoal("Temporary", 10m, 0m));
        Assert.Equal(SavingsGoalMutationStatus.Success, temporary.Status);
        Assert.Equal(SavingsGoalMutationStatus.Success, (await savingsGoals.UpdateGoalAsync(
            temporary.Goal!.Id,
            NewGoal("Temporary renamed", 10m, 0m, temporary.Goal.Id))).Status);
        Assert.Equal(SavingsGoalMutationStatus.Success, await savingsGoals.DeleteGoalAsync(temporary.Goal.Id));
        Assert.Equal(SavingsGoalMutationStatus.Success, (await savingsGoals.ContributeAsync(1, -1m)).Status);
        Assert.Equal(WishlistMutationStatus.Success, (await wishlist.PurchaseWishlistItemAsync(
            1,
            accountId: TestHelpers.AccountIdFor("Rewards"))).Status);
        Assert.Equal(SavingsGoalMutationStatus.Success, (await savingsGoals.FundCurrentCycleAsync()).Status);
        Assert.Equal(SavingsGoalMutationStatus.Success, (await savingsGoals.CompleteGoalAsync(
            1,
            TestHelpers.AccountIdFor("Rewards"))).Status);

        Assert.Equal(7, mutationLock.AcquisitionCount);
    }

    [Fact]
    public async Task SameUserLeaseBlocksUntilTheFirstMutationReleasesIt()
    {
        var mutationLock = new KeyedTestMutationLock("same-user");
        await using var first = await mutationLock.AcquireAsync();

        var secondTask = mutationLock.AcquireAsync();
        await Task.Delay(50);

        Assert.False(secondTask.IsCompleted);

        await first.DisposeAsync();
        await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DifferentUserLeasesDoNotBlockEachOther()
    {
        var firstUserLock = new KeyedTestMutationLock("user-a");
        var secondUserLock = new KeyedTestMutationLock("user-b");
        await using var first = await firstUserLock.AcquireAsync();

        await using var second = await secondUserLock.AcquireAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task LeaseIsReleasedWhenTheMutationThrows()
    {
        var mutationLock = new KeyedTestMutationLock("exception-user");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var lease = await mutationLock.AcquireAsync();
            throw new InvalidOperationException("simulate failed mutation");
        });

        await using var reacquired = await mutationLock.AcquireAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static AppDbContext NewPoolContext()
    {
        var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = TestHelpers.AccountIdFor("Rewards"),
            Name = "Rewards account",
            Bucket = "Rewards",
            Kind = LedgerAccountKind.Bank,
        });
        context.Transactions.Add(new Transaction
        {
            Id = "rewards-seed",
            Date = DateTime.UtcNow.Date,
            PostedAt = DateTime.UtcNow,
            Description = "Rewards seed",
            Category = "Other",
            LedgerCategory = "Rewards",
            Amount = 100m,
            AccountId = TestHelpers.AccountIdFor("Rewards"),
        });
        context.SavingsGoals.Add(NewGoal("Main", 100m, 10m, 1));
        context.WishlistItems.Add(new WishlistItem
        {
            Id = 1,
            Name = "Small reward",
            Price = 10m,
            Priority = "Medium",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        context.SaveChanges();
        return context;
    }

    private static SavingsGoal NewGoal(string name, decimal target, decimal earmarked, int id = 0) => new()
    {
        Id = id,
        Name = name,
        TargetAmount = target,
        EarmarkedAmount = earmarked,
        FundingBucket = SavingsGoalFundingBucket.Rewards,
        TargetDate = DateTime.UtcNow.Date.AddDays(30),
        Priority = "Medium",
        Status = SavingsGoalStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    private sealed class CountingMutationLock : ISharedPoolMutationLock
    {
        public int AcquisitionCount { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            AcquisitionCount++;
            return Task.FromResult<IAsyncDisposable>(NoOpLease.Instance);
        }
    }

    private sealed class KeyedTestMutationLock(string key) : ISharedPoolMutationLock
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
        private readonly SemaphoreSlim _gate = Gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            return new ReleaseLease(_gate);
        }

        private sealed class ReleaseLease(SemaphoreSlim gate) : IAsyncDisposable
        {
            private bool _released;

            public ValueTask DisposeAsync()
            {
                if (!_released)
                {
                    _released = true;
                    gate.Release();
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static readonly NoOpLease Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
