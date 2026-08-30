using System.Text.Json;
using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Tests.Parity;

public sealed class StabilityRecoveryPacingParityTests
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
    public void CohortPacingMatchesCanonicalFixture(JsonElement item)
    {
        var input = item.GetProperty("input");
        var cohorts = input.GetProperty("cohorts").EnumerateArray().Select(cohort =>
            new RecoveryCohortInput(
                cohort.GetProperty("originCycleKey").GetString()!,
                DateOnly.Parse(cohort.GetProperty("fromDate").GetString()!),
                cohort.GetProperty("transactionCount").GetInt32(),
                cohort.GetProperty("remainingShortfall").GetDecimal(),
                cohort.GetProperty("repaidThisCycle").GetDecimal()))
            .ToList();

        var actual = StabilityRecoveryPlanner.ComputeCohortPlan(
            cohorts,
            input.GetProperty("currentCycleKey").GetString()!,
            input.GetProperty("horizon").GetInt32(),
            input.GetProperty("outstandingShortfall").GetDecimal(),
            input.GetProperty("toppedUpThisCycle").GetDecimal());
        var expected = item.GetProperty("expected");

        Assert.Equal(expected.GetProperty("cyclesRemaining").GetInt32(), actual.Aggregate.CyclesRemaining);
        Assert.Equal(expected.GetProperty("requiredThisCycle").GetDecimal(), actual.Aggregate.RequiredThisCycle);
        Assert.Equal(expected.GetProperty("outstandingThisCycle").GetDecimal(), actual.Aggregate.OutstandingThisCycle);
        Assert.Equal(expected.GetProperty("isOverdue").GetBoolean(), actual.Aggregate.IsOverdue);

        var expectedCohorts = expected.GetProperty("cohorts").EnumerateArray().ToList();
        Assert.Equal(expectedCohorts.Count, actual.Cohorts.Count);
        foreach (var (expectedCohort, actualCohort) in expectedCohorts.Zip(actual.Cohorts))
        {
            Assert.Equal(expectedCohort.GetProperty("originCycleKey").GetString(), actualCohort.OriginCycleKey);
            Assert.Equal(expectedCohort.GetProperty("transactionCount").GetInt32(), actualCohort.TransactionCount);
            Assert.Equal(expectedCohort.GetProperty("cyclesRemaining").GetInt32(), actualCohort.CyclesRemaining);
            Assert.Equal(expectedCohort.GetProperty("requiredThisCycle").GetDecimal(), actualCohort.RequiredThisCycle);
            Assert.Equal(expectedCohort.GetProperty("isOverdue").GetBoolean(), actualCohort.IsOverdue);
        }

        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("why").GetString()));
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "stability-recovery-pacing.cases.json");
}
