using System.Collections.Concurrent;
using System.Data.Common;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.SavingsGoals;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FinancialAppApi.Tests;

public sealed class SharedPoolDoubleSpendTests
{
    [Fact]
    public async Task SerializedLockPreventsClaimAndFundFromExceedingThePool()
    {
        await using var fixture = await PoolFixture.CreateAsync();
        var first = fixture.CreateContext();
        var second = fixture.CreateContext();
        await using (first)
        await using (second)
        {
            var sharedLock = new KeyedMutationLock(TestHelpers.DefaultUserId);
            var fundTask = CreateSavingsGoalService(first, sharedLock).FundCurrentCycleAsync();
            var claimTask = CreateWishlistService(second, sharedLock).PurchaseWishlistItemAsync(
                1,
                accountId: TestHelpers.AccountIdFor("Rewards"));

            await Task.WhenAll(fundTask, claimTask);
            Assert.Equal(SavingsGoalMutationStatus.Success, (await fundTask).Status);
            Assert.True((await claimTask).Status is WishlistMutationStatus.Success or WishlistMutationStatus.PriceInvalid);
        }

        await using var readContext = fixture.CreateContext();
        var goal = await readContext.SavingsGoals.SingleAsync();
        var purchase = await readContext.Transactions.SingleOrDefaultAsync(t => t.WishlistItemId == 1);
        var claimed = goal.EarmarkedAmount + Math.Abs(purchase?.Amount ?? 0m);

        Assert.True(claimed <= 100m, $"serialized pool claims totalled {claimed:0.00}");
    }

    [Fact]
    public async Task WithoutTheLock_ConcurrentPreflightReadsCanDoubleSpendThePool()
    {
        var barrier = new PoolReadBarrier();
        await using var fixture = await PoolFixture.CreateAsync(barrier);
        var first = fixture.CreateContext(barrier);
        var second = fixture.CreateContext(barrier);
        await using (first)
        await using (second)
        {
            var disabledLock = new NoOpMutationLock();
            var fundTask = CreateSavingsGoalService(first, disabledLock).FundCurrentCycleAsync();
            var claimTask = CreateWishlistService(second, disabledLock).PurchaseWishlistItemAsync(
                1,
                accountId: TestHelpers.AccountIdFor("Rewards"));

            await Task.WhenAll(fundTask, claimTask);
            Assert.Equal(SavingsGoalMutationStatus.Success, (await fundTask).Status);
            Assert.Equal(WishlistMutationStatus.Success, (await claimTask).Status);
        }

        await using var readContext = fixture.CreateContext();
        var goal = await readContext.SavingsGoals.SingleAsync();
        var purchase = await readContext.Transactions.SingleAsync(t => t.WishlistItemId == 1);
        var claimed = goal.EarmarkedAmount + Math.Abs(purchase.Amount);

        Assert.True(claimed > 100m, $"the disabled-lock race did not reproduce a double claim: {claimed:0.00}");
    }

    private static SavingsGoalService CreateSavingsGoalService(
        AppDbContext context,
        ISharedPoolMutationLock sharedLock) => new(
            context,
            new CycleBalanceService(context),
            NewClock(),
            sharedPoolMutationLock: sharedLock);

    private static WishlistService CreateWishlistService(
        AppDbContext context,
        ISharedPoolMutationLock sharedLock)
    {
        var savingsGoals = CreateSavingsGoalService(context, sharedLock);
        return new WishlistService(
            context,
            new CycleBalanceService(context),
            savingsGoals,
            NewClock(),
            sharedLock);
    }

    private static FinancialClock NewClock() => new(
        TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
        new FixedTimeProvider(new DateTimeOffset(2026, 8, 15, 1, 0, 0, TimeSpan.Zero)));

    private sealed class PoolFixture : IAsyncDisposable
    {
        private readonly string _databaseName = $"shared-pool-{Guid.NewGuid():N}";
        private readonly SqliteConnection _keeper;
        private readonly List<SqliteConnection> _connections = [];

        private PoolFixture(SqliteConnection keeper, string databaseName)
        {
            _keeper = keeper;
            _databaseName = databaseName;
        }

        public static async Task<PoolFixture> CreateAsync(PoolReadBarrier? barrier = null)
        {
            var databaseName = $"shared-pool-{Guid.NewGuid():N}";
            var keeper = new SqliteConnection($"Data Source=file:{databaseName};Mode=Memory;Cache=Shared");
            await keeper.OpenAsync();
            var fixture = new PoolFixture(keeper, databaseName);
            await using var context = fixture.CreateContext(barrier);
            await context.Database.EnsureCreatedAsync();
            context.AppUsers.Add(new AppUser
            {
                Id = TestHelpers.DefaultUserId,
                Username = "shared-pool-user",
                NormalizedUsername = "SHARED-POOL-USER",
                PasswordHash = "not-used",
            });
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
                Date = new DateTime(2026, 8, 15),
                PostedAt = new DateTime(2026, 8, 15, 1, 0, 0, DateTimeKind.Utc),
                Description = "Rewards seed",
                Category = "Other",
                LedgerCategory = "Rewards",
                Amount = 100m,
                AccountId = TestHelpers.AccountIdFor("Rewards"),
            });
            context.SavingsGoals.Add(new SavingsGoal
            {
                Name = "Goal",
                TargetAmount = 100m,
                TargetDate = new DateTime(2026, 8, 20),
                FundingBucket = SavingsGoalFundingBucket.Rewards,
                Priority = "Medium",
                Status = SavingsGoalStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1),
            });
            context.WishlistItems.Add(new WishlistItem
            {
                Id = 1,
                Name = "Reward",
                Price = 100m,
                Priority = "Medium",
                IsActive = true,
                CreatedAt = new DateTime(2026, 1, 1),
            });
            await context.SaveChangesAsync();
            return fixture;
        }

        public AppDbContext CreateContext(PoolReadBarrier? barrier = null)
        {
            var connection = new SqliteConnection($"Data Source=file:{_databaseName};Mode=Memory;Cache=Shared");
            connection.Open();
            _connections.Add(connection);
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection);
            if (barrier is not null) optionsBuilder.AddInterceptors(barrier);
            var options = optionsBuilder.Options;
            var context = new AppDbContext(options);
            context.SetCurrentUser(TestHelpers.DefaultUserId);
            return context;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _connections) await connection.DisposeAsync();
            await _keeper.DisposeAsync();
        }
    }

    private sealed class PoolReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SavingsGoals", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                if (Interlocked.Increment(ref _arrivals) >= 2) _released.TrySetResult(true);
                await _released.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            return result;
        }
    }

    private sealed class NoOpMutationLock : ISharedPoolMutationLock
    {
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(new NoOpLease());
    }

    private sealed class KeyedMutationLock(string key) : ISharedPoolMutationLock
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
        private readonly SemaphoreSlim _gate = Gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            return new Lease(_gate);
        }

        private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
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
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
