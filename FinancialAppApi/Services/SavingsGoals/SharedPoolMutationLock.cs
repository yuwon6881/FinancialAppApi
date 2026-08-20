using FinancialAppApi.Database;

namespace FinancialAppApi.Services.SavingsGoals;

public interface ISharedPoolMutationLock
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Serializes mutations that read and then consume or release a user's bucket balance.
///
/// PostgreSQL advisory locks are session-scoped, so the lease stays held across EF queries,
/// SaveChanges and any surrounding transaction. Non-PostgreSQL providers use a no-op lease for
/// unit tests and local providers that do not implement the advisory-lock functions.
/// </summary>
public sealed class SharedPoolMutationLock : ISharedPoolMutationLock
{
    private readonly AppDbContext _context;

    public SharedPoolMutationLock(AppDbContext context)
    {
        _context = context;
    }

    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_context.CurrentUserId))
        {
            return NoOpLease.Instance;
        }

        var key = $"financial-app:shared-pool:{_context.CurrentUserId}";
        return await PostgresAdvisoryLock.AcquireAsync(_context, key, cancellationToken);
    }

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static readonly NoOpLease Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
