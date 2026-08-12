using FinancialAppApi.Controllers;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Loans;

namespace FinancialAppApi.Tests;

public sealed class LoansControllerTests
{
    [Fact]
    public void MapToDtoKeepsFourDecimalRatesAndOnlyIncludesTheBootstrapPreview()
    {
        var loan = new Loan
        {
            Id = "loan-controller-test",
            Name = "Test loan",
            RecurringPaymentId = "bill-controller-test",
            OpeningPrincipal = 1000m,
            TrackingStartDate = new DateOnly(2026, 1, 31),
            AnnualRatePercent = 3.8875m,
            TermPeriods = 360,
            InterestMethod = LoanInterestMethod.ReducingBalance
        };
        var payment = new RecurringPayment
        {
            Id = loan.RecurringPaymentId,
            Name = "Test bill",
            Frequency = "Monthly",
            DueDate = 31
        };
        var replay = LoanReplay.Replay(loan, payment.Frequency, [], payment.DueDate);

        var dto = LoansController.MapToDto(new LoanView(loan, payment, replay));

        Assert.Equal(3.8875m, dto.AnnualRatePercent);
        Assert.Equal(31, dto.RecurringPaymentDueDate);
        Assert.Equal(6, dto.Snapshot.FutureSchedule.Count);
    }
}
