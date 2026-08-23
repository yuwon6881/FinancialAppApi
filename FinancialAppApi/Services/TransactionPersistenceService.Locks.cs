using Microsoft.EntityFrameworkCore;


namespace FinancialAppApi.Services;

public partial class TransactionPersistenceService
{
    private sealed class NoOpPoolLock : IAsyncDisposable
    {
        public static readonly NoOpPoolLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task<IAsyncDisposable> AcquireRecurringLocksAsync(
        IEnumerable<string?> recurringPaymentIds,
        CancellationToken cancellationToken)
    {
        var leases = new List<IAsyncDisposable>();
        try
        {
            foreach (var paymentId in recurringPaymentIds
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Select(id => id!)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(id => id, StringComparer.Ordinal))
            {
                leases.Add(await _recurringPaymentMutationLock.AcquireAsync(paymentId, cancellationToken));
            }
            return new CompositeLease(leases);
        }
        catch
        {
            for (var i = leases.Count - 1; i >= 0; i--) await leases[i].DisposeAsync();
            throw;
        }
    }

    private async Task<bool> IsLoanRepaymentActionTransactionAsync(
        string transactionId,
        CancellationToken cancellationToken) =>
        (await LoanRepaymentActionTransactionIdsAsync(cancellationToken)).Contains(transactionId);

    private async Task<HashSet<string>> LoanRepaymentActionTransactionIdsAsync(CancellationToken cancellationToken)
    {
        var values = await _context.LoanRepaymentActions.AsNoTracking()
            .Select(action => action.TransactionIds)
            .ToListAsync(cancellationToken);
        return values
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed class CompositeLease(IReadOnlyList<IAsyncDisposable> leases) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            for (var i = leases.Count - 1; i >= 0; i--) await leases[i].DisposeAsync();
        }
    }
}
