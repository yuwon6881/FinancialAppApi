namespace FinancialAppApi.Services;

/// <summary>
/// Supplies the user's financial calendar date independently of the server/container timezone.
/// Security timestamps remain UTC instants; only date-based financial behavior uses this clock.
/// </summary>
public sealed class FinancialClock
{
    private readonly TimeProvider _timeProvider;

    public FinancialClock(IConfiguration configuration, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        var configuredId = configuration["Financial:TimeZoneId"];
        if (string.IsNullOrWhiteSpace(configuredId))
        {
            throw new InvalidOperationException("Financial:TimeZoneId must be configured.");
        }

        TimeZone = TimeZoneInfo.FindSystemTimeZoneById(configuredId.Trim());
    }

    private FinancialClock(TimeZoneInfo timeZone, TimeProvider timeProvider)
    {
        TimeZone = timeZone;
        _timeProvider = timeProvider;
    }

    public static FinancialClock Utc { get; } = new(TimeZoneInfo.Utc, TimeProvider.System);

    public TimeZoneInfo TimeZone { get; }

    public DateTime LocalNow => TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), TimeZone).DateTime;

    public DateOnly Today => DateOnly.FromDateTime(LocalNow);
}
