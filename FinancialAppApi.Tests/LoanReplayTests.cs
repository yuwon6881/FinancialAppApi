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

    [Fact]
    public void MultiplePartialPaymentsForSameOccurrenceDate_AreAggregatedCorrectly()
    {
        var loan = NewLoan(rate: 12m);
        // Scheduled payment is ~88.85 (interest is 10, principal is 78.85)
        // Tx1 = 30 (covers 10 interest, 20 principal)
        // Tx2 = 40 (covers 0 interest, 40 principal)
        var result = LoanReplay.Replay(loan, [
            new(new DateOnly(2026, 1, 1), new DateTime(2026, 1, 2, 10, 0, 0), 30m, TransactionId: "tx-1"),
            new(new DateOnly(2026, 1, 1), new DateTime(2026, 1, 5, 14, 0, 0), 40m, TransactionId: "tx-2"),
        ]);

        Assert.Equal(2, result.Payments.Count);
        Assert.Equal(10m, result.Payments[0].Interest);
        Assert.Equal(20m, result.Payments[0].Principal);
        Assert.Equal(980m, result.Payments[0].BalanceAfter);

        Assert.Equal(0m, result.Payments[1].Interest);
        Assert.Equal(40m, result.Payments[1].Principal);
        Assert.Equal(940m, result.Payments[1].BalanceAfter);

        Assert.Equal(940m, result.OutstandingBalance);
        // Because 30 + 40 = 70 < scheduled (~88.85), the occurrence is incomplete,
        // so future schedule starts with Jan 1 for the remaining ~18.85!
        Assert.Equal(new DateOnly(2026, 1, 1), result.FutureSchedule[0].OccurrenceDate);
        Assert.Equal(18.85m, result.FutureSchedule[0].Payment);
        Assert.Equal(0m, result.FutureSchedule[0].Interest);
        Assert.Equal(18.85m, result.FutureSchedule[0].Principal);

        // Subsequent future occurrence is Feb 1
        Assert.Equal(new DateOnly(2026, 2, 1), result.FutureSchedule[1].OccurrenceDate);
    }

    [Fact]
    public void CompletedOccurrence_AdvancesFutureScheduleToNextPeriod()
    {
        var loan = NewLoan(rate: 12m);
        // Pay full scheduled payment or more on Jan 1
        var result = LoanReplay.Replay(loan, [
            new(new DateOnly(2026, 1, 1), new DateTime(2026, 1, 2, 10, 0, 0), 50m, TransactionId: "tx-1"),
            new(new DateOnly(2026, 1, 1), new DateTime(2026, 1, 5, 14, 0, 0), 50m, TransactionId: "tx-2"),
        ]);

        Assert.Equal(2, result.Payments.Count);
        Assert.Equal(10m, result.Payments[0].Interest);
        Assert.Equal(40m, result.Payments[0].Principal);
        Assert.Equal(0m, result.Payments[1].Interest);
        Assert.Equal(50m, result.Payments[1].Principal);
        Assert.Equal(910m, result.OutstandingBalance);

        // Future schedule starts on Feb 1
        Assert.Equal(new DateOnly(2026, 2, 1), result.FutureSchedule[0].OccurrenceDate);
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
