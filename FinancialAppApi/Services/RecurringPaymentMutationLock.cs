using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

public interface IRecurringPaymentMutationLock
{
    Task<IAsyncDisposable> AcquireAsync(string recurringPaymentId, CancellationToken cancellationToken = default);
}

public sealed class RecurringPaymentMutationLock : IRecurringPaymentMutationLock
{
    private readonly AppDbContext _context;

    public RecurringPaymentMutationLock(AppDbContext context)
    {
        _context = context;
    }

    public Task<IAsyncDisposable> AcquireAsync(
        string recurringPaymentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_context.CurrentUserId) || string.IsNullOrWhiteSpace(recurringPaymentId))
        {
            return Task.FromResult<IAsyncDisposable>(NoOpLease.Instance);
        }

        return PostgresAdvisoryLock.AcquireAsync(
            _context,
            $"financial-app:recurring-payment:{_context.CurrentUserId}:{recurringPaymentId}",
            cancellationToken);
    }

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static readonly NoOpLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
