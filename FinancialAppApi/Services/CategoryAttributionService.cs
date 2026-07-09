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
            var endDate = startDate.AddMonths(1).AddDays(-1);
            var lbl = $"{startDate:MMM} {GetDayWithSuffix(startDate.Day)} ~ {endDate:MMM} {GetDayWithSuffix(endDate.Day)}, {startDate.Year}";
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

        if (cycleDay > 1 && date.Day < cycleDay)
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
                if (categoryName.Equals("Essentials", StringComparison.OrdinalIgnoreCase)) decimal.TryParse(parts[0], out pct);
                else if (categoryName.Equals("Growth", StringComparison.OrdinalIgnoreCase)) decimal.TryParse(parts[1], out pct);
                else if (categoryName.Equals("Stability", StringComparison.OrdinalIgnoreCase)) decimal.TryParse(parts[2], out pct);
                else if (categoryName.Equals("Rewards", StringComparison.OrdinalIgnoreCase)) decimal.TryParse(parts[3], out pct);

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
