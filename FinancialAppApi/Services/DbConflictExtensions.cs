using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinancialAppApi.Services;

internal static class DbConflictExtensions
{
    // Only meaningful against PostgreSQL: the EF Core InMemory provider used in tests never
    // throws DbUpdateException for a uniqueness violation, so callers must also keep an
    // application-level pre-check for tests to exercise the conflict path.
    public static bool IsUniqueViolation(this DbUpdateException exception) =>
        exception.InnerException is PostgresException postgresException &&
        postgresException.SqlState == PostgresErrorCodes.UniqueViolation;
}
