using FinancialAppApi.Models;
using FinancialAppApi.Services.Loans;

namespace FinancialAppApi.Tests;

public sealed class LoanTermScheduleTests
{
    [Fact]
    public void MonthlySchedule_ClampsTheAnchorAndCountsTheEndDateInclusively()
    {
        var loan = NewLoan("Monthly", 31, new DateOnly(2026, 1, 31), 18);

        Assert.True(LoanTermSchedule.TryCountPaymentsThrough(
            loan,
            new DateOnly(2026, 6, 30),
            out var count));
        Assert.Equal(6, count);
        Assert.True(LoanTermSchedule.TryGetEndDate(loan, out var endDate));
        Assert.Equal(new DateOnly(2027, 6, 30), endDate);
    }

    [Fact]
    public void AnnualSchedule_UsesTheCapturedMonthAndSerializesYearsAsPeriods()
    {
        var loan = NewLoan("Annually", 29, new DateOnly(2024, 2, 29), 3);

        Assert.True(LoanTermSchedule.TryGetEndDate(loan, out var endDate));
        Assert.Equal(new DateOnly(2026, 2, 28), endDate);
        Assert.True(LoanTermSchedule.TryCountPaymentsThrough(loan, endDate, out var count));
        Assert.Equal(3, count);
    }

    private static Loan NewLoan(string frequency, int dueDay, DateOnly startDate, int termPeriods) => new()
    {
        Id = "loan-term",
        Name = "Term loan",
        RecurringPaymentId = "bill-term",
        OpeningPrincipal = 1000m,
        TrackingStartDate = startDate,
        AnnualRatePercent = 0m,
        TermPeriods = termPeriods,
        InterestMethod = LoanInterestMethod.ReducingBalance,
        ScheduleFrequency = frequency,
        ScheduleDueDay = dueDay,
        ScheduleStartDate = startDate,
        ScheduleStatus = LoanScheduleStatus.Complete
    };
}
