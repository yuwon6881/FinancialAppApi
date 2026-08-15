using System.Text.Json;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Loans;

namespace FinancialAppApi.Tests.Parity;

public sealed class LoanReplayParityTests
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
    public void LoanReplayMatchesCanonicalFixture(JsonElement item)
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
            TrackingStartDate = DateOnly.Parse(loanJson.GetProperty("trackingStartDate").GetString()!),
            ScheduleFrequency = loanJson.GetProperty("scheduleFrequency").GetString()!,
            ScheduleDueDay = loanJson.GetProperty("scheduleDueDay").GetInt32(),
            ScheduleStartDate = DateOnly.Parse(loanJson.GetProperty("scheduleStartDate").GetString()!),
            ScheduleStatus = loanJson.GetProperty("scheduleStatus").GetString()!,
        };

        var paymentInputs = input.GetProperty("inputs").EnumerateArray().Select(p => new LoanPaymentInput(
            OccurrenceDate: DateOnly.Parse(p.GetProperty("occurrenceDate").GetString()!),
            PostedAt: DateTime.UtcNow,
            Amount: p.GetProperty("amount").GetDecimal(),
            IsDiscarded: p.GetProperty("isDiscarded").GetBoolean()
        )).ToList();

        var result = LoanReplay.Replay(loan, paymentInputs);

        var expected = item.GetProperty("expected");
        Assert.Equal(expected.GetProperty("outstandingBalance").GetDecimal(), result.OutstandingBalance);
        Assert.Equal(expected.GetProperty("scheduledPayment").GetDecimal(), result.ScheduledPayment);
        Assert.Equal(expected.GetProperty("totalScheduledInterest").GetDecimal(), result.TotalScheduledInterest);
        Assert.Equal(expected.GetProperty("totalInterestPaid").GetDecimal(), result.TotalInterestPaid);

        var expPayoff = expected.GetProperty("payoffDate").ValueKind == JsonValueKind.Null
            ? (DateOnly?)null
            : DateOnly.Parse(expected.GetProperty("payoffDate").GetString()!);
        Assert.Equal(expPayoff, result.PayoffDate);

        var expLast = expected.GetProperty("lastOccurrenceDate").ValueKind == JsonValueKind.Null
            ? (DateOnly?)null
            : DateOnly.Parse(expected.GetProperty("lastOccurrenceDate").GetString()!);
        Assert.Equal(expLast, result.LastOccurrenceDate);

        Assert.Equal(expected.GetProperty("paymentCount").GetInt32(), result.Payments.Count);
        Assert.Equal(expected.GetProperty("futureScheduleCount").GetInt32(), result.FutureSchedule.Count);
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "loan-replay.cases.json");
}
