using System.Text.Json;
using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Tests.Parity;

public sealed class StabilityReloadParityTests
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
    public void StabilityReloadMatchesCanonicalFixture(JsonElement item)
    {
        var id = item.GetProperty("id").GetString()!;
        var why = item.GetProperty("why").GetString()!;
        var input = item.GetProperty("input");

        var openingJson = input.GetProperty("opening");
        var openingObligations = openingJson.GetProperty("obligations").EnumerateArray().Select(o => new ReloadObligation(
            o.GetProperty("transactionId").GetString()!,
            o.GetProperty("originalAmount").GetDecimal(),
            o.GetProperty("remainingAmount").GetDecimal(),
            o.TryGetProperty("date", out var d) && d.ValueKind != JsonValueKind.Null ? DateOnly.Parse(d.GetString()!) : null
        )).ToList();

        DateOnly? oldestDate = openingJson.GetProperty("oldestOutstandingDate").ValueKind == JsonValueKind.Null
            ? null
            : DateOnly.Parse(openingJson.GetProperty("oldestOutstandingDate").GetString()!);

        var opening = new ReloadState(
            Outstanding: openingJson.GetProperty("outstanding").GetDecimal(),
            OldestOutstandingDate: oldestDate,
            MarkedThisRun: 0m,
            RepaidThisRun: 0m,
            Obligations: openingObligations);

        var openingBalance = input.GetProperty("openingBalance").GetDecimal();
        var target = input.GetProperty("target").GetDecimal();

        var movements = input.GetProperty("movements").EnumerateArray().Select(m => new ReloadMovement(
            DateOnly.Parse(m.GetProperty("date").GetString()!),
            m.GetProperty("change").GetDecimal(),
            m.GetProperty("repayment").GetDecimal(),
            m.GetProperty("marked").GetBoolean(),
            m.TryGetProperty("id", out var mid) ? mid.GetString() : null
        )).ToList();

        var result = StabilityReloadLedger.Replay(opening, openingBalance, target, movements);

        var expected = item.GetProperty("expected");
        Assert.Equal(expected.GetProperty("outstanding").GetDecimal(), result.Outstanding);
        Assert.Equal(expected.GetProperty("markedThisRun").GetDecimal(), result.MarkedThisRun);
        Assert.Equal(expected.GetProperty("repaidThisRun").GetDecimal(), result.RepaidThisRun);

        var expOldest = expected.GetProperty("oldestOutstandingDate").ValueKind == JsonValueKind.Null
            ? (DateOnly?)null
            : DateOnly.Parse(expected.GetProperty("oldestOutstandingDate").GetString()!);
        Assert.Equal(expOldest, result.OldestOutstandingDate);
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "stability-reload.cases.json");
}
