using System.Globalization;

namespace FinancialAppApi.Database;

public static class TransactionDate
{
    public static DateTime FromInputDate(DateOnly date)
    {
        var now = DateTime.UtcNow;
        return DateTime.SpecifyKind(date.ToDateTime(TimeOnly.FromTimeSpan(now.TimeOfDay)), DateTimeKind.Utc);
    }

    public static DateTime PreserveTimeWhenSameDate(DateTime existing, DateOnly date)
    {
        var currentDate = DateOnly.FromDateTime(existing);
        if (currentDate == date)
        {
            return DateTime.SpecifyKind(existing, DateTimeKind.Utc);
        }

        return FromInputDate(date);
    }

    public static DateOnly ToDateOnly(DateTime timestamp) => DateOnly.FromDateTime(timestamp);

    public static bool TryParseInputDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static DateTime StartOfDate(DateOnly date) =>
        DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    public static DateTime ExclusiveEndOfDate(DateOnly date) => StartOfDate(date).AddDays(1);
}
