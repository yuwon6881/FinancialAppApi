using System.Data;
using System.Data.Common;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

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
        if (!IsPostgres || string.IsNullOrWhiteSpace(_context.CurrentUserId))
        {
            return NoOpLease.Instance;
        }

        var connection = _context.Database.GetDbConnection();
        var openedByLease = connection.State != ConnectionState.Open;
        if (openedByLease)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var key = $"financial-app:shared-pool:{_context.CurrentUserId}";
        try
        {
            await ExecuteAsync(connection, "pg_advisory_lock", key, cancellationToken);
            return new Lease(_context, connection, key, openedByLease);
        }
        catch
        {
            if (openedByLease)
            {
                await connection.CloseAsync();
            }

            throw;
        }
    }

    private bool IsPostgres => _context.Database.ProviderName?.Contains(
        "Npgsql",
        StringComparison.OrdinalIgnoreCase) == true;

    private static async Task ExecuteAsync(
        DbConnection connection,
        string function,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {function}(hashtextextended(@lock_key, 0));";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.DbType = DbType.String;
        parameter.Value = key;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly AppDbContext _context;
        private readonly DbConnection _connection;
        private readonly string _key;
        private readonly bool _openedByLease;
        private bool _released;

        public Lease(AppDbContext context, DbConnection connection, string key, bool openedByLease)
        {
            _context = context;
            _connection = connection;
            _key = key;
            _openedByLease = openedByLease;
        }

        public async ValueTask DisposeAsync()
        {
            if (_released) return;
            _released = true;

            if (_connection.State == ConnectionState.Open)
            {
                await ExecuteAsync(_connection, "pg_advisory_unlock", _key, CancellationToken.None);
            }

            if (_openedByLease && _context.Database.CurrentTransaction == null)
            {
                await _connection.CloseAsync();
            }
        }
    }

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static readonly NoOpLease Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
