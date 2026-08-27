namespace FinancialAppApi.Services;

/// <summary>
/// Supplies the user's financial calendar date independently of the server/container timezone.
/// Security timestamps remain UTC instants; only date-based financial behavior uses this clock.
/// </summary>
public sealed class FinancialClock
{
    private static readonly HashSet<string> MalaysiaTimeZoneIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Asia/Kuala_Lumpur",
        "Malaysia Standard Time"
    };

    private readonly TimeProvider _timeProvider;

    public FinancialClock(IConfiguration configuration, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        var configuredId = configuration["Financial:TimeZoneId"];
        if (string.IsNullOrWhiteSpace(configuredId))
        {
            throw new InvalidOperationException("Financial:TimeZoneId must be configured.");
        }

        TimeZone = ResolveTimeZone(configuredId.Trim());
    }

    private FinancialClock(TimeZoneInfo timeZone, TimeProvider timeProvider)
    {
        TimeZone = timeZone;
        _timeProvider = timeProvider;
    }

    public static FinancialClock Utc { get; } = new(TimeZoneInfo.Utc, TimeProvider.System);

    public TimeZoneInfo TimeZone { get; }

    /// <summary>
    /// The same instant as <see cref="LocalNow"/>, expressed in UTC. Callers that derive a stored
    /// UTC timestamp from a window measured against <see cref="LocalNow"/> or <see cref="Today"/>
    /// must read it from here rather than <c>DateTime.UtcNow</c>: two clocks agree in production
    /// and disagree under an injected TimeProvider, which is exactly where the arithmetic is
    /// checked.
    /// </summary>
    public DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public DateTime LocalNow => TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), TimeZone).DateTime;

    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    internal static TimeZoneInfo ResolveTimeZone(
        string id,
        Func<string, TimeZoneInfo>? systemResolver = null)
    {
        try
        {
            return (systemResolver ?? TimeZoneInfo.FindSystemTimeZoneById)(id);
        }
        catch (Exception ex) when (
            ex is TimeZoneNotFoundException or InvalidTimeZoneException &&
            MalaysiaTimeZoneIds.Contains(id))
        {
            // The minimal .NET chiseled runtime intentionally omits /usr/share/zoneinfo.
            // Malaysia has observed UTC+8 year-round since 1982, and this clock is used
            // for current financial calendar behavior rather than historical conversion.
            return TimeZoneInfo.CreateCustomTimeZone(
                id,
                TimeSpan.FromHours(8),
                "(UTC+08:00) Kuala Lumpur",
                "Malaysia Time");
        }
    }
}
