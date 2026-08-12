using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Loans;

/// <summary>
/// Keeps the persisted payment-period count aligned with the linked bill's inclusive end date.
/// The loan's captured cadence is authoritative so later bill cadence edits cannot silently
/// rewrite the amortization schedule.
/// </summary>
public static class LoanTermSchedule
{
    public static bool TryCountPaymentsThrough(Loan loan, DateOnly endDate, out int count)
    {
        count = 0;
        if (!TryFirstOccurrence(loan, out var occurrence) || endDate < occurrence) return false;

        while (occurrence <= endDate && count <= 360)
        {
            count++;
            occurrence = AddPeriod(occurrence, loan.ScheduleFrequency!, loan.ScheduleDueDay!.Value);
        }

        return count is >= 1 and <= 360;
    }

    public static bool TryGetEndDate(Loan loan, out DateOnly endDate)
    {
        endDate = default;
        if (loan.TermPeriods is < 1 or > 360 || !TryFirstOccurrence(loan, out var occurrence))
            return false;

        for (var index = 1; index < loan.TermPeriods; index++)
        {
            occurrence = AddPeriod(occurrence, loan.ScheduleFrequency!, loan.ScheduleDueDay!.Value);
        }

        endDate = occurrence;
        return true;
    }

    private static bool TryFirstOccurrence(Loan loan, out DateOnly occurrence)
    {
        occurrence = default;
        if (loan.ScheduleStatus == LoanScheduleStatus.Incomplete
            || loan.ScheduleStartDate == null
            || loan.ScheduleDueDay is not (>= 1 and <= 31)
            || loan.ScheduleFrequency is not "Monthly" and not "Annually")
            return false;

        var start = loan.ScheduleStartDate.Value;
        var annual = loan.ScheduleFrequency == "Annually";
        if (annual)
        {
            var year = Math.Max(start.Year, loan.TrackingStartDate.Year);
            occurrence = AnchoredDate(year, start.Month, loan.ScheduleDueDay.Value);
            if (occurrence < start || occurrence < loan.TrackingStartDate)
                occurrence = AnchoredDate(year + 1, start.Month, loan.ScheduleDueDay.Value);
            return true;
        }

        var candidate = new DateOnly(
            loan.TrackingStartDate.Year,
            loan.TrackingStartDate.Month,
            Math.Min(loan.ScheduleDueDay.Value, DateTime.DaysInMonth(
                loan.TrackingStartDate.Year,
                loan.TrackingStartDate.Month)));
        if (candidate < start || candidate < loan.TrackingStartDate)
        {
            var nextMonth = new DateOnly(candidate.Year, candidate.Month, 1).AddMonths(1);
            candidate = AnchoredDate(nextMonth.Year, nextMonth.Month, loan.ScheduleDueDay.Value);
        }
        while (candidate < start)
        {
            candidate = AddPeriod(candidate, "Monthly", loan.ScheduleDueDay.Value);
        }
        occurrence = candidate;
        return true;
    }

    private static DateOnly AddPeriod(DateOnly date, string frequency, int dueDay)
    {
        var target = frequency == "Annually" ? date.AddYears(1) : date.AddMonths(1);
        return AnchoredDate(target.Year, target.Month, dueDay);
    }

    private static DateOnly AnchoredDate(int year, int month, int dueDay) =>
        new(year, month, Math.Min(dueDay, DateTime.DaysInMonth(year, month)));
}
