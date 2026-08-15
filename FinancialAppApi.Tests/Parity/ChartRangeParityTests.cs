using System.Text.Json;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests.Parity;

public sealed class ChartRangeParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.Clone()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ChartRangeMatchesCanonicalFixture(JsonElement item)
    {
        var today = DateOnly.Parse(item.GetProperty("today").GetString()!);
        var expectedStart = item.GetProperty("expectedStart").ValueKind == JsonValueKind.Null
            ? (DateOnly?)null
            : DateOnly.Parse(item.GetProperty("expectedStart").GetString()!);
        var range = item.GetProperty("range").GetString()!;
        var values = Enumerable.Range(0, item.GetProperty("inputPoints").GetInt32()).ToList();

        Assert.Equal(expectedStart, InvestmentChartRange.StartFor(range, today));
        Assert.Equal(item.GetProperty("expectedPoints").GetInt32(), InvestmentChartRange.Sample(values).Count);
        Assert.Equal(values[^1], InvestmentChartRange.Sample(values)[^1]);
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "chart-range.cases.json");
}
