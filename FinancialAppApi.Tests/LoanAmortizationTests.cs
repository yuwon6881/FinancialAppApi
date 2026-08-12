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
        var split = LoanAmortization.ApplyPayment(loan, "Monthly", new DateOnly(2026, 1, 1), 1200m, 100m, 1, 0m);
        Assert.Equal(100m, split.Principal);
        Assert.Equal(0m, split.Interest);
    }

    [Fact]
    public void FlatInterestUsesTheFinalResidualCent()
    {
        var loan = NewLoan(opening: 1000m, rate: 11m, term: 3, method: LoanInterestMethod.Flat);

        Assert.Equal(27.5m, LoanAmortization.TotalScheduledInterest(loan, "Monthly"));
        var first = LoanAmortization.ApplyScheduledPayment(loan, "Monthly", new DateOnly(2026, 1, 1), 1000m, 1, 0m);
        var second = LoanAmortization.ApplyScheduledPayment(loan, "Monthly", new DateOnly(2026, 2, 1), first.BalanceAfter, 2, first.Interest);
        var third = LoanAmortization.ApplyScheduledPayment(loan, "Monthly", new DateOnly(2026, 3, 1), second.BalanceAfter, 3, first.Interest + second.Interest);

        Assert.Equal(9.17m, first.Interest);
        Assert.Equal(9.17m, second.Interest);
        Assert.Equal(9.16m, third.Interest);
        Assert.Equal(0m, third.BalanceAfter);
    }

    [Fact]
    public void UnderpaymentDoesNotCapitalizeOrReduceTheBalance()
    {
        var loan = NewLoan(opening: 1000m, rate: 12m, term: 12);

        var split = LoanAmortization.ApplyPayment(loan, "Monthly", new DateOnly(2026, 1, 1), 1000m, 5m, 1, 0m);

        Assert.Equal(5m, split.Interest);
        Assert.Equal(0m, split.Principal);
        Assert.Equal(1000m, split.BalanceAfter);
        Assert.True(split.PaymentDidNotCoverInterest);
    }

    [Fact]
    public void OverpaymentSurfacesTheSurplus()
    {
        var loan = NewLoan(opening: 1000m, rate: 0m, term: 12);

        var split = LoanAmortization.ApplyPayment(loan, "Monthly", new DateOnly(2026, 1, 1), 1000m, 1200m, 1, 0m);

        Assert.Equal(1000m, split.Principal);
        Assert.Equal(0m, split.BalanceAfter);
        Assert.Equal(200m, split.Surplus);
    }

    [Theory]
    [InlineData(25, 300)]
    [InlineData(100, 360)]
    public void HighAnnualRatesDoNotOverflowThePaymentFormula(decimal rate, int term)
    {
        var loan = NewLoan(opening: 1000m, rate: rate, term: term);

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
