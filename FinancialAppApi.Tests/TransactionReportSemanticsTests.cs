using FinancialAppApi.Models;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public sealed class TransactionReportSemanticsTests
{
    [Fact]
    public void ReportableCashMovement_ExcludesStructuralRowsAndZero()
    {
        Assert.True(TransactionReportSemantics.IsReportableCashMovement(100m, "Food", "Essentials"));
        Assert.False(TransactionReportSemantics.IsReportableCashMovement(100m, "Transfer", "Essentials"));
        Assert.False(TransactionReportSemantics.IsReportableCashMovement(-100m, "Adjustment", "Essentials"));
        Assert.False(TransactionReportSemantics.IsReportableCashMovement(100m, "Food", "Discarded"));
        Assert.False(TransactionReportSemantics.IsReportableCashMovement(0m, "Food", "Essentials"));
    }

    [Theory]
    [InlineData(500, "Income", true)]
    [InlineData(500, "IncomeSplit:50,25,15,10", true)]
    [InlineData(500, "Essentials", false)]
    [InlineData(500, "Transfer:Income->Essentials", false)]
    [InlineData(-500, "Income", false)]
    public void IsReportableIncome_RequiresPositiveIncomeLedgerRow(decimal amount, string ledgerCategory, bool expected)
    {
        var transaction = new Transaction
        {
            Amount = amount,
            Category = "Food",
            LedgerCategory = ledgerCategory
        };

        Assert.Equal(expected, TransactionReportSemantics.IsReportableIncome(transaction));
    }
}
