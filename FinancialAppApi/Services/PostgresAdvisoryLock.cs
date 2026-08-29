using System.Data;
using System.Data.Common;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinancialAppApi.Services;

internal static class PostgresAdvisoryLock
{
    public static async Task<IAsyncDisposable> AcquireAsync(
        AppDbContext context,
        string key,
        CancellationToken cancellationToken)
    {
        if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
        {
            return NoOpLease.Instance;
        }

        var connection = context.Database.GetDbConnection();
        var openedByLease = connection.State != ConnectionState.Open;
        if (openedByLease) await connection.OpenAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, "pg_advisory_lock", key, cancellationToken);
            return new Lease(context, connection, key, openedByLease);
        }
        catch
        {
            if (openedByLease) await connection.CloseAsync();
            throw;
        }
    }

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
            try
            {
                if (_connection.State == ConnectionState.Open)
                {
                    await ExecuteAsync(_connection, "pg_advisory_unlock", _key, CancellationToken.None);
                }
            }
            catch
            {
                // NoResetOnClose deliberately avoids a remote reset on the hot path. If the
                // explicit unlock cannot be confirmed, never let this possibly lock-bearing
                // connector serve another request.
                if (_connection is NpgsqlConnection npgsqlConnection)
                {
                    NpgsqlConnection.ClearPool(npgsqlConnection);
                }
                throw;
            }
            finally
            {
                if (_openedByLease && _context.Database.CurrentTransaction == null)
                {
                    await _connection.CloseAsync();
                }
            }
        }
    }

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static readonly NoOpLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
