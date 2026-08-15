using System.Text.Json;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests.Parity;

public sealed class CycleMathParityTests
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
    public void CycleMathMatchesCanonicalFixture(JsonElement item)
    {
        var id = item.GetProperty("id").GetString()!;
        var why = item.GetProperty("why").GetString()!;
        var input = item.GetProperty("input");
        var kind = input.GetProperty("kind").GetString()!;
        var expected = item.GetProperty("expected");

        if (kind == "range")
        {
            var year = input.GetProperty("year").GetInt32();
            var monthIndex = input.GetProperty("monthIndex").GetInt32();
            var cycleDay = input.GetProperty("cycleDay").GetInt32();

            var (start, end, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);

            var expStart = expected.GetProperty("startDate").GetString();
            var expEnd = expected.GetProperty("endDate").GetString();

            Assert.True(start.ToString("yyyy-MM-dd") == expStart, $"{id}: {why} start mismatch");
            Assert.True(end.ToString("yyyy-MM-dd") == expEnd, $"{id}: {why} end mismatch");
        }
        else if (kind == "forDate")
        {
            var dateStr = input.GetProperty("date").GetString()!;
            var cycleDay = input.GetProperty("cycleDay").GetInt32();
            var date = DateOnly.Parse(dateStr);

            var (year, monthIdx) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(date, cycleDay);

            var expYear = expected.GetProperty("year").GetInt32();
            var expMonth = expected.GetProperty("monthIndex").GetInt32();

            Assert.True(year == expYear, $"{id}: {why} year mismatch");
            Assert.True(monthIdx == expMonth, $"{id}: {why} monthIndex mismatch");
        }
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "cycle-math.cases.json");
}
