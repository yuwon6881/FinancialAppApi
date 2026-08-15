using System.Text.Json;
using FinancialAppApi.Models;
using FinancialAppApi.Services.SavingsGoals;

namespace FinancialAppApi.Tests.Parity;

public sealed class FreeRewardsParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [
                item.GetProperty("id").GetString()!,
                item.GetProperty("why").GetString()!,
                item.Clone(),
            ];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FreeRewardsMatchesSharedFixture(string id, string why, JsonElement item)
    {
        _ = id;
        _ = why;
        var input = item.GetProperty("input");
        var goals = input.GetProperty("goals").EnumerateArray();
        var totalEarmarked = goals
            .Where(goal => goal.GetProperty("status").GetString() == SavingsGoalStatus.Active
                && !goal.GetProperty("isPendingDelete").GetBoolean()
                && goal.GetProperty("fundingBucket").GetString() == SavingsGoalFundingBucket.Rewards)
            .Sum(goal => goal.GetProperty("earmarkedAmount").GetDecimal());
        var occurrences = input.GetProperty("pendingOccurrences").EnumerateArray()
            .Select((occurrence, index) => new RecurringPaymentOccurrence
            {
                Id = $"parity-{index}",
                RecurringPaymentId = $"payment-{index}",
                Name = "Parity bill",
                Status = occurrence.GetProperty("status").GetString()!,
                LedgerCategory = occurrence.GetProperty("ledgerCategory").GetString(),
                Category = occurrence.GetProperty("category").GetString(),
                ScheduledAmount = occurrence.GetProperty("scheduledAmount").GetDecimal(),
            })
            .ToArray();
        var pending = SavingsGoalPacing.PendingAmount(occurrences, SavingsGoalFundingBucket.Rewards);
        var unassigned = SavingsGoalPacing.Unassigned(
            input.GetProperty("rewardsBalance").GetDecimal(),
            totalEarmarked,
            pending);
        var expected = item.GetProperty("expected");

        Assert.Equal(expected.GetProperty("pendingRewards").GetDecimal(), pending);
        Assert.Equal(expected.GetProperty("unassigned").GetDecimal(), unassigned);
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Parity", "Fixtures", "free-rewards.cases.json");
}
