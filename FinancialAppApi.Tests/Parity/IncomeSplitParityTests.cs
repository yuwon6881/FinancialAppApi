using System.Text.Json;
using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Tests.Parity;

public sealed class IncomeSplitParityTests
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
    public void IncomeSplitMatchesCanonicalFixture(JsonElement item)
    {
        var id = item.GetProperty("id").GetString()!;
        var why = item.GetProperty("why").GetString()!;
        var input = item.GetProperty("input");

        var amount = input.GetProperty("amount").GetDecimal();
        var essentialsAlloc = input.GetProperty("essentialsAlloc").GetDecimal();
        var growthAlloc = input.GetProperty("growthAlloc").GetDecimal();
        var stabilityAlloc = input.GetProperty("stabilityAlloc").GetDecimal();
        var rewardsAlloc = input.GetProperty("rewardsAlloc").GetDecimal();
        var stabilityBalance = input.GetProperty("stabilityBalance").GetDecimal();
        var stabilityTarget = input.GetProperty("stabilityTarget").GetDecimal();
        var stabilityOverflowRedirect = input.GetProperty("stabilityOverflowRedirect").GetString();
        var recoveryTopUp = input.GetProperty("recoveryTopUp").GetDecimal();

        var spec = IncomeSplitPlanner.Resolve(
            amount,
            essentialsAlloc,
            growthAlloc,
            stabilityAlloc,
            rewardsAlloc,
            stabilityBalance,
            stabilityTarget,
            stabilityOverflowRedirect,
            recoveryTopUp);

        var expected = item.GetProperty("expected");
        var expectedSpecString = expected.GetProperty("specString").GetString();

        Assert.True(spec.ToSpecString() == expectedSpecString, $"{id}: {why} expected {expectedSpecString}, got {spec.ToSpecString()}");
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "income-split.cases.json");
}
