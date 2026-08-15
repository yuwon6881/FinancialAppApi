using System.Text.Json;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests.Parity;

public sealed class ContributionPlanParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.Clone()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ContributionPlanMatchesCanonicalFixture(JsonElement item)
    {
        var input = item.GetProperty("input");
        var values = ReadMap(input.GetProperty("values"));
        var targets = ReadMap(input.GetProperty("targets"));
        var plan = InvestmentAllocationService.BuildContributionPlan(
            input.GetProperty("appCurrency").GetString()!,
            input.GetProperty("investedValue").GetDecimal(),
            values,
            targets,
            input.GetProperty("minimumNewMoney").GetDecimal(),
            input.GetProperty("restoreTarget").GetBoolean(),
            input.GetProperty("routineContribution").ValueKind == JsonValueKind.Null
                ? null
                : input.GetProperty("routineContribution").GetDecimal(),
            input.GetProperty("cyclesObserved").GetInt32(),
            input.GetProperty("availableCash").GetDecimal());

        var expected = item.GetProperty("expected");
        Assert.NotNull(plan);
        Assert.Equal(expected.GetProperty("amount").GetDecimal(), plan!.Amount);
        Assert.Equal(expected.GetProperty("routineContribution").GetDecimal(), plan.RoutineContribution);
        Assert.Equal(expected.GetProperty("isEstimated").GetBoolean(), plan.IsEstimated);
        foreach (var sleeve in plan.Sleeves)
            Assert.Equal(expected.GetProperty("sleeves").GetProperty(sleeve.Sleeve).GetDecimal(), sleeve.Amount);
    }

    private static Dictionary<string, decimal> ReadMap(JsonElement value)
        => value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetDecimal());

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "contribution-plan.cases.json");
}
