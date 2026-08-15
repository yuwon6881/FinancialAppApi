using System.Text.Json;

namespace FinancialAppApi.Tests.Parity;

public sealed class NotificationTagParityTests
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
    public void NotificationTagMatchesCanonicalFixture(JsonElement item)
    {
        var id = item.GetProperty("id").GetString()!;
        var why = item.GetProperty("why").GetString()!;
        var input = item.GetProperty("input");
        var kind = input.GetProperty("kind").GetString()!;

        string tag;
        if (kind == "category-limit")
        {
            var cycleKey = input.GetProperty("cycleKey").GetString()!;
            tag = $"category-limit-{cycleKey}";
        }
        else
        {
            var paymentId = input.GetProperty("recurringPaymentId").GetString()!;
            var occurrenceDate = input.GetProperty("occurrenceDate").GetString()!;
            tag = $"recurring-reminder-{paymentId}-{occurrenceDate}";
        }

        var expectedTag = item.GetProperty("expected").GetProperty("tag").GetString()!;
        Assert.Equal(expectedTag, tag);
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "notification-tag.cases.json");
}
