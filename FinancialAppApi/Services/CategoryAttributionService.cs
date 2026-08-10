using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services;

public static class CategoryAttributionService
{
    private static readonly string[] Months = FinancialConstants.MonthAbbreviations;

    public static (DateTime start, DateTime end, string label) GetCycleRange(int year, int monthIndex, int cycleDay)
    {
        if (cycleDay == 1)
        {
            var start = new DateTime(year, monthIndex, 1);
            var end = start.AddMonths(1).AddDays(-1);
            var lbl = $"{start:MMM dd} ~ {end:MMM dd, yyyy}";
            return (start, end, lbl);
        }
        else
        {
            var startDayActual = Math.Min(cycleDay, DateTime.DaysInMonth(year, monthIndex));
            var startDate = new DateTime(year, monthIndex, startDayActual);
            // The end is the day before the NEXT cycle's clamped start, so consecutive
            // cycles tile with no gaps or overlaps. Deriving it from this month's start
            // (start.AddMonths(1).AddDays(-1)) drops days whenever cycleDay > 28 and the
            // two months clamp to different day counts (e.g. May 30 falling into no cycle).
            var nextMonth = new DateTime(year, monthIndex, 1).AddMonths(1);
            var nextStartDay = Math.Min(cycleDay, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
            var endDate = new DateTime(nextMonth.Year, nextMonth.Month, nextStartDay).AddDays(-1);
            var lbl = startDate.Year == endDate.Year
                ? $"{startDate:MMM} {GetDayWithSuffix(startDate.Day)} ~ {endDate:MMM} {GetDayWithSuffix(endDate.Day)}, {startDate.Year}"
                : $"{startDate:MMM} {GetDayWithSuffix(startDate.Day)}, {startDate.Year} ~ {endDate:MMM} {GetDayWithSuffix(endDate.Day)}, {endDate.Year}";
            return (startDate, endDate, lbl);
        }
    }

    public static (string month, int year) GetCycleMonthAndYearForDate(DateTime date, int cycleDay)
    {
        var (year, monthIdx) = GetCycleYearAndMonthIndexForDate(DateOnly.FromDateTime(date), cycleDay);
        return (Months[monthIdx - 1], year);
    }

    public static (int year, int monthIndex) GetCycleYearAndMonthIndexForDate(DateOnly date, int cycleDay)
    {
        int year = date.Year;
        int monthIdx = date.Month;

        // Compare against the cycle start clamped to THIS month's length, matching
        // GetCycleRange. A raw "date.Day < cycleDay" would, for cycleDay > 28,
        // attribute a clamped last-of-month day (e.g. Feb 28 with cycleDay 31) to the
        // previous cycle even though GetCycleRange places it in the current one.
        var clampedStart = Math.Min(cycleDay, DateTime.DaysInMonth(year, monthIdx));
        if (cycleDay > 1 && date.Day < clampedStart)
        {
            monthIdx--;
            if (monthIdx < 1)
            {
                monthIdx = 12;
                year--;
            }
        }

        return (year, monthIdx);
    }

    public static DateTime GetBillingDateForCycle(DateTime cycleStart, DateTime cycleEnd, int cycleDay, int dueDate)
    {
        if (cycleDay == 1 || dueDate >= cycleStart.Day)
        {
            return new DateTime(cycleStart.Year, cycleStart.Month, Math.Min(dueDate, DateTime.DaysInMonth(cycleStart.Year, cycleStart.Month)));
        }
        return new DateTime(cycleEnd.Year, cycleEnd.Month, Math.Min(dueDate, DateTime.DaysInMonth(cycleEnd.Year, cycleEnd.Month)));
    }

    public static decimal GetCategoryAmount(Transaction t, string categoryName)
    {
        if (string.Equals(t.LedgerCategory, categoryName, StringComparison.OrdinalIgnoreCase))
        {
            return t.Amount;
        }
        if (!string.IsNullOrEmpty(t.LedgerCategory) && t.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = t.LedgerCategory.Substring("IncomeSplit:".Length).Split(',');
            if (parts.Length == 4)
            {
                decimal pct = 0;
                // Percentages are written with InvariantCulture (TransactionPersistenceService);
                // parse them the same way so hosts with a comma decimal separator don't read
                // "12.5" as 125.
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (categoryName.Equals("Essentials", StringComparison.OrdinalIgnoreCase)) decimal.TryParse(parts[0], System.Globalization.NumberStyles.Any, ci, out pct);
                else if (categoryName.Equals("Growth", StringComparison.OrdinalIgnoreCase)) decimal.TryParse(parts[1], System.Globalization.NumberStyles.Any, ci, out pct);
                else if (categoryName.Equals("Stability", StringComparison.OrdinalIgnoreCase)) decimal.TryParse(parts[2], System.Globalization.NumberStyles.Any, ci, out pct);
                else if (categoryName.Equals("Rewards", StringComparison.OrdinalIgnoreCase)) decimal.TryParse(parts[3], System.Globalization.NumberStyles.Any, ci, out pct);

                return t.Amount * (pct / 100m);
            }
        }
        if (!string.IsNullOrEmpty(t.LedgerCategory) && t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = t.LedgerCategory.Substring("Transfer:".Length).Split(new[] { "->" }, StringSplitOptions.None);
            if (parts.Length == 2)
            {
                var source = parts[0].Trim();
                var target = parts[1].Trim();

                if (string.Equals(categoryName, source, StringComparison.OrdinalIgnoreCase))
                {
                    return -Math.Abs(t.Amount);
                }
                if (string.Equals(categoryName, target, StringComparison.OrdinalIgnoreCase))
                {
                    return Math.Abs(t.Amount);
                }
            }
        }
        return 0;
    }

    private static string GetDayWithSuffix(int day)
    {
        if (day >= 11 && day <= 13) return $"{day}th";
        return (day % 10) switch
        {
            1 => $"{day}st",
            2 => $"{day}nd",
            3 => $"{day}rd",
            _ => $"{day}th"
        };
    }
}
