using FinancialAppApi.Models;
using FinancialAppApi.Services.Loans;

namespace FinancialAppApi.Tests;

public sealed class LoanAmortizationTests
{
    [Fact]
    public void ZeroRateReducingBalanceUsesEqualPayments()
    {
        var loan = NewLoan(opening: 1200m, rate: 0m, term: 12);

        Assert.Equal(100m, LoanAmortization.ScheduledPayment(loan, "Monthly"));
        var split = LoanAmortization.ApplyPayment(loan, "Monthly", new DateOnly(2026, 1, 1), 1200m, 100m, 1, 0m, new DateOnly(2026, 1, 1));
        Assert.Equal(100m, split.Principal);
        Assert.Equal(0m, split.Interest);
    }

    [Fact]
    public void FlatInterestUsesTheFinalResidualCent()
    {
        var loan = NewLoan(opening: 1000m, rate: 11m, term: 3, method: LoanInterestMethod.Flat);

        Assert.Equal(27.5m, LoanAmortization.TotalScheduledInterest(loan, "Monthly"));
        var first = LoanAmortization.ApplyScheduledPayment(loan, "Monthly", new DateOnly(2026, 1, 1), 1000m, 1, 0m, new DateOnly(2026, 1, 1));
        var second = LoanAmortization.ApplyScheduledPayment(loan, "Monthly", new DateOnly(2026, 2, 1), first.BalanceAfter, 2, first.Interest, new DateOnly(2026, 1, 1));
        var third = LoanAmortization.ApplyScheduledPayment(loan, "Monthly", new DateOnly(2026, 3, 1), second.BalanceAfter, 3, first.Interest + second.Interest, new DateOnly(2026, 2, 1));

        Assert.Equal(9.17m, first.Interest);
        Assert.Equal(9.17m, second.Interest);
        Assert.Equal(9.16m, third.Interest);
        Assert.Equal(0m, third.BalanceAfter);
    }

    [Fact]
    public void UnderpaymentDoesNotCapitalizeOrReduceTheBalance()
    {
        var loan = NewLoan(opening: 1000m, rate: 12m, term: 12);

        var split = LoanAmortization.ApplyPayment(loan, "Monthly", new DateOnly(2026, 1, 1), 1000m, 5m, 1, 0m, new DateOnly(2026, 1, 1));

        Assert.Equal(5m, split.Interest);
        Assert.Equal(0m, split.Principal);
        Assert.Equal(1000m, split.BalanceAfter);
        Assert.True(split.PaymentDidNotCoverInterest);
    }

    [Fact]
    public void OverpaymentSurfacesTheSurplus()
    {
        var loan = NewLoan(opening: 1000m, rate: 0m, term: 12);

        var split = LoanAmortization.ApplyPayment(loan, "Monthly", new DateOnly(2026, 1, 1), 1000m, 1200m, 1, 0m, new DateOnly(2026, 1, 1));

        Assert.Equal(1000m, split.Principal);
        Assert.Equal(0m, split.BalanceAfter);
        Assert.Equal(200m, split.Surplus);
    }

    [Fact]
    public void DailyRestUsesTheExactOccurrenceWindow()
    {
        var loan = NewLoan(opening: 1000m, rate: 12m, term: 12, method: LoanInterestMethod.ReducingBalanceDaily);

        var fourteenDays = LoanAmortization.ApplyPayment(
            loan, "Monthly", new DateOnly(2026, 1, 15), 1000m, 100m, 1, 0m,
            new DateOnly(2026, 1, 1));
        var thirtyOneDays = LoanAmortization.ApplyPayment(
            loan, "Monthly", new DateOnly(2026, 2, 1), 1000m, 100m, 1, 0m,
            new DateOnly(2026, 1, 1));
        var monthly = LoanAmortization.ApplyPayment(
            NewLoan(opening: 1000m, rate: 12m, term: 12), "Monthly", new DateOnly(2026, 1, 15),
            1000m, 100m, 1, 0m, new DateOnly(2026, 1, 1));

        Assert.Equal(4.6m, fourteenDays.Interest);
        Assert.Equal(10.19m, thirtyOneDays.Interest);
        Assert.Equal(10m, monthly.Interest);
        Assert.NotEqual(monthly.Interest, fourteenDays.Interest);
    }

    [Fact]
    public void DailyRestChargesNothingForAZeroDayWindow()
    {
        var loan = NewLoan(opening: 1000m, rate: 12m, term: 12, method: LoanInterestMethod.ReducingBalanceDaily);

        var split = LoanAmortization.ApplyPayment(
            loan, "Monthly", new DateOnly(2026, 1, 1), 1000m, 100m, 1, 0m,
            new DateOnly(2026, 1, 1));

        Assert.Equal(0m, split.Interest);
        Assert.Equal(100m, split.Principal);
    }

    [Fact]
    public void DailyRestUsesTheMonthlyRestQuoteForPaymentAndTotalInterest()
    {
        var monthly = NewLoan(opening: 1000m, rate: 12m, term: 12);
        var daily = NewLoan(opening: 1000m, rate: 12m, term: 12, method: LoanInterestMethod.ReducingBalanceDaily);

        Assert.Equal(
            LoanAmortization.ScheduledPayment(monthly, "Monthly"),
            LoanAmortization.ScheduledPayment(daily, "Monthly"));
        Assert.Equal(
            LoanAmortization.TotalScheduledInterest(monthly, "Monthly"),
            LoanAmortization.TotalScheduledInterest(daily, "Monthly"));
    }

    [Fact]
    public void InterestOnlyKeepsScheduledPaymentsOnTheBalanceButAllowsOverpayment()
    {
        var loan = NewLoan(opening: 1000m, rate: 12m, term: 12, method: LoanInterestMethod.InterestOnly);

        var scheduled = LoanAmortization.ApplyScheduledPayment(
            loan, "Monthly", new DateOnly(2026, 2, 1), 1000m, 1, 0m, new DateOnly(2026, 1, 1));
        var overpayment = LoanAmortization.ApplyPayment(
            loan, "Monthly", new DateOnly(2026, 1, 1), 1000m, 25m, 1, 0m,
            new DateOnly(2026, 1, 1));

        Assert.Equal(10m, scheduled.Interest);
        Assert.Equal(0m, scheduled.Principal);
        Assert.Equal(1000m, scheduled.BalanceAfter);
        Assert.Equal(15m, overpayment.Principal);
        Assert.Equal(985m, overpayment.BalanceAfter);
    }

    [Theory]
    [InlineData(25, 300, LoanInterestMethod.ReducingBalance)]
    [InlineData(25, 300, LoanInterestMethod.ReducingBalanceDaily)]
    [InlineData(25, 300, LoanInterestMethod.Flat)]
    [InlineData(25, 300, LoanInterestMethod.InterestOnly)]
    [InlineData(100, 360, LoanInterestMethod.ReducingBalance)]
    [InlineData(100, 360, LoanInterestMethod.ReducingBalanceDaily)]
    [InlineData(100, 360, LoanInterestMethod.Flat)]
    [InlineData(100, 360, LoanInterestMethod.InterestOnly)]
    public void HighAnnualRatesDoNotOverflowThePaymentFormula(decimal rate, int term, string method)
    {
        var loan = NewLoan(opening: 1000m, rate: rate, term: term, method: method);

        var payment = LoanAmortization.ScheduledPayment(loan, "Annually");

        Assert.True(payment > 0m);
        Assert.Equal(payment, LoanAmortization.RoundMoney(payment));
    }

    private static Loan NewLoan(
        decimal opening,
        decimal rate,
        int term,
        string method = LoanInterestMethod.ReducingBalance) => new()
        {
            Id = "loan-test",
            Name = "Test loan",
            RecurringPaymentId = "bill-test",
            OpeningPrincipal = opening,
            TrackingStartDate = new DateOnly(2026, 1, 1),
            AnnualRatePercent = rate,
            TermPeriods = term,
            InterestMethod = method
        };
}
