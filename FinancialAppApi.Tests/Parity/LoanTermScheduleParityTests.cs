using System.Text.Json;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Loans;

namespace FinancialAppApi.Tests.Parity;

public sealed class LoanTermScheduleParityTests
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
    public void LoanTermScheduleMatchesCanonicalFixture(JsonElement item)
    {
        var id = item.GetProperty("id").GetString()!;
        var why = item.GetProperty("why").GetString()!;
        var input = item.GetProperty("input");
        var kind = input.GetProperty("kind").GetString()!;
        var loanJson = input.GetProperty("loan");

        var loan = new Loan
        {
            TrackingStartDate = DateOnly.Parse(loanJson.GetProperty("trackingStartDate").GetString()!),
            ScheduleFrequency = loanJson.GetProperty("scheduleFrequency").GetString()!,
            ScheduleDueDay = loanJson.GetProperty("scheduleDueDay").GetInt32(),
            ScheduleStartDate = DateOnly.Parse(loanJson.GetProperty("scheduleStartDate").GetString()!),
            ScheduleStatus = loanJson.GetProperty("scheduleStatus").GetString()!,
            TermPeriods = loanJson.TryGetProperty("termPeriods", out var tp) ? tp.GetInt32() : 0,
        };

        var expected = item.GetProperty("expected");

        if (kind == "endDate")
        {
            var success = LoanTermSchedule.TryGetEndDate(loan, out var endDate);
            var expEndDate = expected.GetProperty("endDate").ValueKind == JsonValueKind.Null
                ? (DateOnly?)null
                : DateOnly.Parse(expected.GetProperty("endDate").GetString()!);

            if (expEndDate == null)
            {
                Assert.False(success, $"{id}: {why} expected failure");
            }
            else
            {
                Assert.True(success, $"{id}: {why} expected success");
                Assert.Equal(expEndDate.Value, endDate);
            }
        }
        else if (kind == "countThrough")
        {
            var targetEndDate = DateOnly.Parse(input.GetProperty("endDate").GetString()!);
            var success = LoanTermSchedule.TryCountPaymentsThrough(loan, targetEndDate, out var count);
            var expCount = expected.GetProperty("count").ValueKind == JsonValueKind.Null
                ? (int?)null
                : expected.GetProperty("count").GetInt32();

            if (expCount == null)
            {
                Assert.False(success, $"{id}: {why} expected failure");
            }
            else
            {
                Assert.True(success, $"{id}: {why} expected success");
                Assert.Equal(expCount.Value, count);
            }
        }
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "loan-term-schedule.cases.json");
}
