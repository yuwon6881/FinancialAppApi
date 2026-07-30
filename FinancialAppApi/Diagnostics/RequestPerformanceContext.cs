namespace FinancialAppApi.Diagnostics;

/// <summary>
/// Accumulates database work for one dependency-injection scope. For HTTP requests this is
/// consumed by <see cref="Middleware.RequestPerformanceMiddleware"/>; background scopes still
/// benefit from the slow-command logging performed by the EF interceptor.
/// </summary>
public sealed class RequestPerformanceContext
{
    private long _databaseDurationTicks;
    private int _databaseCommandCount;

    public int DatabaseCommandCount => Volatile.Read(ref _databaseCommandCount);

    public double DatabaseDurationMilliseconds =>
        TimeSpan.FromTicks(Interlocked.Read(ref _databaseDurationTicks)).TotalMilliseconds;

    public void RecordDatabaseCommand(TimeSpan duration)
    {
        Interlocked.Increment(ref _databaseCommandCount);
        Interlocked.Add(ref _databaseDurationTicks, duration.Ticks);
    }
}
