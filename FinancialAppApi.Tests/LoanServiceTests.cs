using FinancialAppApi.Models;
using FinancialAppApi.Services.Loans;

namespace FinancialAppApi.Tests;

public sealed class LoanServiceTests
{
    [Fact]
    public async Task GetLoansAsync_ReplaysLinkedSettlementHistory()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment());
        context.Loans.Add(NewLoan());
        context.Transactions.Add(new Transaction
        {
            Id = "tx-loan-1",
            Date = new DateTime(2026, 1, 2),
            PostedAt = new DateTime(2026, 1, 2),
            Description = "Loan bill",
            Category = "Bills",
            LedgerCategory = "Essentials",
            Amount = -100m,
            RecurringPaymentId = "bill-loan",
            RecurringOccurrenceDate = new DateOnly(2026, 1, 1)
        });
        await context.SaveChangesAsync();

        var view = Assert.Single(await new LoanService(context).GetLoansAsync());

        Assert.Equal(900m, view.Replay.OutstandingBalance);
        Assert.Equal("Monthly", view.RecurringPayment?.Frequency);
        Assert.Equal(new DateOnly(2026, 1, 1), view.Replay.Payments[0].OccurrenceDate);
    }

    [Fact]
    public async Task GetLoansAsync_SurvivesDanglingRecurringPaymentLink()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Loans.Add(NewLoan());
        await context.SaveChangesAsync();

        var view = Assert.Single(await new LoanService(context).GetLoansAsync());

        Assert.Null(view.RecurringPayment);
        Assert.Equal("bill-loan", view.Loan.RecurringPaymentId);
    }

    [Fact]
    public async Task GetLoansAsync_TreatsDiscardedLedgerCategoryCaseInsensitively()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment());
        context.Loans.Add(NewLoan());
        context.Transactions.Add(new Transaction
        {
            Id = "tx-loan-discarded",
            Date = new DateTime(2026, 1, 2),
            PostedAt = new DateTime(2026, 1, 2),
            Description = "Discarded loan bill",
            Category = "Bills",
            LedgerCategory = "discarded",
            Amount = -100m,
            RecurringPaymentId = "bill-loan",
            RecurringOccurrenceDate = new DateOnly(2026, 1, 1)
        });
        await context.SaveChangesAsync();

        var view = Assert.Single(await new LoanService(context).GetLoansAsync());

        Assert.Empty(view.Replay.Payments);
        Assert.Equal(1000m, view.Replay.OutstandingBalance);
    }

    [Fact]
    public async Task CreateLoanAsync_RejectsASecondLoanForTheSameBill()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment());
        context.Loans.Add(NewLoan());
        await context.SaveChangesAsync();

        var second = NewLoan();
        second.Id = "loan-two";
        var result = await new LoanService(context).CreateLoanAsync(second);

        Assert.Equal(LoanMutationStatus.RecurringPaymentAlreadyLinked, result.Status);
    }

    private static RecurringPayment NewPayment() => new()
    {
        Id = "bill-loan",
        Name = "Loan bill",
        Amount = -100m,
        Frequency = "Monthly",
        Category = "Bills",
        LedgerCategory = "Essentials",
        DueDate = 1,
        StartDate = "2026-01-01",
        Active = true
    };

    private static Loan NewLoan() => new()
    {
        Id = "loan-one",
        Name = "Test loan",
        RecurringPaymentId = "bill-loan",
        OpeningPrincipal = 1000m,
        TrackingStartDate = new DateOnly(2026, 1, 1),
        AnnualRatePercent = 0m,
        TermPeriods = 10,
        InterestMethod = LoanInterestMethod.ReducingBalance
    };
}
