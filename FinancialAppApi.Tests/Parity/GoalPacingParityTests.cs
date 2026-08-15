using System.Text.Json;
using FinancialAppApi.Models;
using FinancialAppApi.Services.SavingsGoals;

namespace FinancialAppApi.Tests.Parity;

public sealed class GoalPacingParityTests
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
    public void GoalPacingMatchesSharedFixture(string id, string why, JsonElement item)
    {
        _ = id;
        _ = why;
        var input = item.GetProperty("input");
        var goalInput = input.GetProperty("goal");
        var goal = new SavingsGoal
        {
            Id = goalInput.GetProperty("id").GetInt32(),
            Name = "Parity goal",
            TargetAmount = goalInput.GetProperty("targetAmount").GetDecimal(),
            EarmarkedAmount = goalInput.GetProperty("earmarkedAmount").GetDecimal(),
            TargetDate = DateTime.Parse(input.GetProperty("targetDate").GetString()!),
            Status = goalInput.GetProperty("status").GetString()!,
            CycleFundedKey = goalInput.GetProperty("cycleFundedKey").ValueKind == JsonValueKind.Null
                ? null
                : goalInput.GetProperty("cycleFundedKey").GetString(),
            CycleFundedAmount = goalInput.GetProperty("cycleFundedAmount").GetDecimal(),
        };
        var pace = SavingsGoalPacing.ComputePace(
            goal,
            DateOnly.Parse(input.GetProperty("today").GetString()!),
            input.GetProperty("cycleDay").GetInt32(),
            input.GetProperty("currentCycleKey").GetString()!);
        var expected = item.GetProperty("expected");

        Assert.Equal(expected.GetProperty("remaining").GetDecimal(), pace.Remaining);
        Assert.Equal(expected.GetProperty("cyclesRemaining").GetInt32(), pace.CyclesRemaining);
        Assert.Equal(expected.GetProperty("requiredPerCycle").GetDecimal(), pace.RequiredPerCycle);
        Assert.Equal(expected.GetProperty("fundedThisCycle").GetDecimal(), pace.FundedThisCycle);
        Assert.Equal(expected.GetProperty("outstandingThisCycle").GetDecimal(), pace.OutstandingThisCycle);
        Assert.Equal(expected.GetProperty("isOverdue").GetBoolean(), pace.IsOverdue);
        Assert.Equal(expected.GetProperty("isFunded").GetBoolean(), pace.IsFunded);
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Parity", "Fixtures", "goal-pacing.cases.json");
}
