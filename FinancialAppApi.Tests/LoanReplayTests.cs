using FinancialAppApi.Models;
using FinancialAppApi.Services.Loans;

namespace FinancialAppApi.Tests;

public sealed class LoanReplayTests
{
    [Fact]
    public void OrdersByOccurrenceDateBeforePostedAt()
    {
        var loan = NewLoan(rate: 12m);
        var result = LoanReplay.Replay(loan, [
            new(new DateOnly(2026, 2, 1), new DateTime(2026, 2, 1), 100m, TransactionId: "feb"),
            new(new DateOnly(2026, 1, 1), new DateTime(2026, 3, 1), 100m, TransactionId: "jan"),
        ]);

        Assert.Equal("jan", result.Payments[0].TransactionId);
        Assert.Equal(10m, result.Payments[0].Interest);
        Assert.Equal(9.1m, result.Payments[1].Interest);
    }

    [Fact]
    public void DiscardedOccurrenceSkipsPaymentButMovesTheScheduleForward()
    {
        var loan = NewLoan(rate: 0m);
        var result = LoanReplay.Replay(loan, [
            new(new DateOnly(2026, 1, 1), DateTime.MinValue, 0m, IsDiscarded: true),
        ]);

        Assert.Empty(result.Payments);
        Assert.Equal(new DateOnly(2026, 2, 1), result.FutureSchedule[0].OccurrenceDate);
    }

    [Fact]
    public void DeletingAnEarlierSettlementRecomputesLaterInterestAndPayoff()
    {
        var loan = NewLoan(rate: 12m);
        var withBoth = LoanReplay.Replay(loan, [
            new(new DateOnly(2026, 1, 1), DateTime.MinValue, 100m, TransactionId: "one"),
            new(new DateOnly(2026, 2, 1), DateTime.MinValue, 100m, TransactionId: "two"),
        ]);
        var afterDelete = LoanReplay.Replay(loan, [
            new(new DateOnly(2026, 2, 1), DateTime.MinValue, 100m, TransactionId: "two"),
        ]);

        Assert.True(afterDelete.OutstandingBalance > withBoth.OutstandingBalance);
        Assert.Equal(10m, afterDelete.Payments[0].Interest);
    }

    [Fact]
    public void PaymentThatDoesNotCoverInterestDoesNotPoisonALaterPayoff()
    {
        var loan = NewLoan(rate: 12m);
        var result = LoanReplay.Replay(loan, [
            new(new DateOnly(2026, 1, 1), DateTime.MinValue, 5m, TransactionId: "under"),
        ]);

        Assert.Equal(1000m, result.OutstandingBalance);
        Assert.NotNull(result.PayoffDate);
        Assert.Equal(0m, result.FutureSchedule[^1].BalanceAfter);
    }

    [Fact]
    public void ExactInterestPaymentIsNotMarkedAsAnUnderpayment()
    {
        var loan = NewLoan(rate: 12m);
        var split = LoanAmortization.ApplyPayment(
            loan,
            "Monthly",
            new DateOnly(2026, 1, 1),
            1000m,
            10m,
            1,
            0m,
            previousAccrualDate: new DateOnly(2026, 1, 1));

        Assert.False(split.PaymentDidNotCoverInterest);
        Assert.Equal(0m, split.Principal);
    }

    [Fact]
    public void MonthlyScheduleClampsToTheRecurringPaymentAnchorDay()
    {
        var loan = NewLoan(rate: 0m);
        loan.ScheduleDueDay = 31;
        var result = LoanReplay.Replay(loan, [
            new(new DateOnly(2026, 1, 31), DateTime.MinValue, 0m, IsDiscarded: true),
        ]);

        Assert.Equal(new DateOnly(2026, 2, 28), result.FutureSchedule[0].OccurrenceDate);
        Assert.Equal(new DateOnly(2026, 5, 31), result.FutureSchedule[3].OccurrenceDate);
    }

    [Fact]
    public void PayoffDateStaysAtTheFirstDebtFreeOccurrence()
    {
        var loan = NewLoan(rate: 0m);
        var result = LoanReplay.Replay(loan, [
            new(new DateOnly(2026, 1, 1), DateTime.MinValue, 1000m, TransactionId: "payoff"),
            new(new DateOnly(2026, 2, 1), DateTime.MinValue, 50m, TransactionId: "surplus"),
        ]);

        Assert.Equal(new DateOnly(2026, 1, 1), result.PayoffDate);
        Assert.Equal(0m, result.OutstandingBalance);
    }

    [Fact]
    public void FirstMonthlyScheduleUsesTheBillAnchorAfterTrackingStarts()
    {
        var loan = NewLoan(rate: 0m);
        loan.TrackingStartDate = new DateOnly(2026, 1, 12);

        loan.ScheduleDueDay = 15;
        var result = LoanReplay.Replay(loan, []);

        Assert.Equal(new DateOnly(2026, 1, 15), result.FutureSchedule[0].OccurrenceDate);
    }

    [Fact]
    public void FirstAnnualScheduleUsesTheBillMonthAndAnchor()
    {
        var loan = NewLoan(rate: 0m);
        loan.TrackingStartDate = new DateOnly(2026, 4, 1);
        loan.ScheduleStartDate = new DateOnly(2026, 1, 1);
        loan.ScheduleFrequency = "Annually";
        loan.ScheduleDueDay = 15;

        var result = LoanReplay.Replay(loan, []);

        Assert.Equal(new DateOnly(2027, 1, 15), result.FutureSchedule[0].OccurrenceDate);
    }

    [Fact]
    public void DiscardedOccurrenceDaysRemainInTheNextDailyRestWindow()
    {
        var loan = NewLoan(rate: 12m);
        loan.InterestMethod = LoanInterestMethod.ReducingBalanceDaily;
        var result = LoanReplay.Replay(loan, [
            new(new DateOnly(2026, 2, 1), DateTime.MinValue, 0m, IsDiscarded: true),
            new(new DateOnly(2026, 3, 1), DateTime.MinValue, 100m, TransactionId: "march"),
        ]);

        Assert.Equal(19.4m, result.Payments[0].Interest);
    }

    [Fact]
    public void InterestOnlyForecastStopsAtTheTermAndLeavesTheBalloonUnpaid()
    {
        var loan = NewLoan(rate: 12m);
        loan.InterestMethod = LoanInterestMethod.InterestOnly;
        loan.TermPeriods = 3;

        var result = LoanReplay.Replay(loan, []);

        Assert.Equal(3, result.FutureSchedule.Count);
        Assert.Null(result.PayoffDate);
        Assert.Equal(1000m, result.OutstandingBalance);
    }

    private static Loan NewLoan(decimal rate) => new()
    {
        Id = "loan-test",
        Name = "Test loan",
        RecurringPaymentId = "bill-test",
        OpeningPrincipal = 1000m,
        TrackingStartDate = new DateOnly(2026, 1, 1),
        ScheduleStatus = LoanScheduleStatus.Complete,
        ScheduleFrequency = "Monthly",
        ScheduleDueDay = 1,
        ScheduleStartDate = new DateOnly(2026, 1, 1),
        AnnualRatePercent = rate,
        TermPeriods = 12,
        InterestMethod = LoanInterestMethod.ReducingBalance
    };
}
