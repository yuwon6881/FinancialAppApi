using System.Text.Json;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Loans;

namespace FinancialAppApi.Tests.Parity;

public sealed class LoanAmortizationParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [item.Clone()];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LoanAmortizationMatchesCanonicalFixture(JsonElement item)
    {
        var id = item.GetProperty("id").GetString()!;
        var why = item.GetProperty("why").GetString()!;
        var input = item.GetProperty("input");
        var loanJson = input.GetProperty("loan");

        var loan = new Loan
        {
            OpeningPrincipal = loanJson.GetProperty("openingPrincipal").GetDecimal(),
            AnnualRatePercent = loanJson.GetProperty("annualRatePercent").GetDecimal(),
            TermPeriods = loanJson.GetProperty("termPeriods").GetInt32(),
            InterestMethod = loanJson.GetProperty("interestMethod").GetString()!,
        };

        var frequency = input.GetProperty("frequency").GetString()!;
        var occurrenceDate = DateOnly.Parse(input.GetProperty("occurrenceDate").GetString()!);
        var balanceBefore = input.GetProperty("balanceBefore").GetDecimal();
        var payment = input.GetProperty("payment").GetDecimal();
        var paymentNumber = input.GetProperty("paymentNumber").GetInt32();
        var flatInterestPaidBefore = input.GetProperty("flatInterestPaidBefore").GetDecimal();
        var previousAccrualDate = DateOnly.Parse(input.GetProperty("previousAccrualDate").GetString()!);

        var expected = item.GetProperty("expected");

        var scheduledPayment = LoanAmortization.ScheduledPayment(loan, frequency);
        Assert.Equal(expected.GetProperty("scheduledPayment").GetDecimal(), scheduledPayment);

        var totalScheduledInterest = LoanAmortization.TotalScheduledInterest(loan, frequency);
        Assert.Equal(expected.GetProperty("totalScheduledInterest").GetDecimal(), totalScheduledInterest);

        var split = LoanAmortization.ApplyPayment(
            loan,
            frequency,
            occurrenceDate,
            balanceBefore,
            payment,
            paymentNumber,
            flatInterestPaidBefore,
            previousAccrualDate);

        var expSplit = expected.GetProperty("split");
        Assert.Equal(expSplit.GetProperty("payment").GetDecimal(), split.Payment);
        Assert.Equal(expSplit.GetProperty("interest").GetDecimal(), split.Interest);
        Assert.Equal(expSplit.GetProperty("principal").GetDecimal(), split.Principal);
        Assert.Equal(expSplit.GetProperty("balanceBefore").GetDecimal(), split.BalanceBefore);
        Assert.Equal(expSplit.GetProperty("balanceAfter").GetDecimal(), split.BalanceAfter);
        Assert.Equal(expSplit.GetProperty("surplus").GetDecimal(), split.Surplus);
        Assert.Equal(expSplit.GetProperty("paymentDidNotCoverInterest").GetBoolean(), split.PaymentDidNotCoverInterest);
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "loan-amortization.cases.json");
}
