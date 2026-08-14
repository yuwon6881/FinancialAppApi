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
}
