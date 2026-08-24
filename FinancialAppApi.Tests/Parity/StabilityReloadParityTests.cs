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
            m.TryGetProperty("id", out var mid) ? mid.GetString() : null,
            m.TryGetProperty("postedAt", out var posted) && posted.ValueKind != JsonValueKind.Null
                ? DateTime.Parse(
                    posted.GetString()!,
                    null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                        | System.Globalization.DateTimeStyles.AssumeUniversal)
                : null
        )).ToList();

        // A case may carry an effective-dated plan timeline instead of one flat target.
        var planPoints = input.TryGetProperty("planPoints", out var pointsJson)
            ? pointsJson.EnumerateArray().Select(p => new ReloadPlanPoint(
                DateTime.Parse(
                    p.GetProperty("effectiveAt").GetString()!,
                    null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                        | System.Globalization.DateTimeStyles.AssumeUniversal),
                p.GetProperty("target").GetDecimal())).ToList()
            : [new ReloadPlanPoint(DateTime.MinValue, target)];

        var result = StabilityReloadLedger.Replay(opening, openingBalance, planPoints, movements);

        var expected = item.GetProperty("expected");
        Assert.Equal(expected.GetProperty("outstanding").GetDecimal(), result.Outstanding);
        Assert.Equal(expected.GetProperty("markedThisRun").GetDecimal(), result.MarkedThisRun);
        Assert.Equal(expected.GetProperty("repaidThisRun").GetDecimal(), result.RepaidThisRun);
        Assert.Equal(expected.GetProperty("openMarkedTotal").GetDecimal(), result.OpenMarkedTotal);
        Assert.Equal(expected.GetProperty("openRepaidTotal").GetDecimal(), result.OpenRepaidTotal);
        // The reported totals must always account for exactly what is owed.
        Assert.Equal(result.Outstanding, result.OpenMarkedTotal - result.OpenRepaidTotal);

        var expOldest = expected.GetProperty("oldestOutstandingDate").ValueKind == JsonValueKind.Null
            ? (DateOnly?)null
            : DateOnly.Parse(expected.GetProperty("oldestOutstandingDate").GetString()!);
        Assert.Equal(expOldest, result.OldestOutstandingDate);

        var expectedObligations = expected.GetProperty("obligations").EnumerateArray().ToList();
        Assert.Equal(
            expectedObligations.Select(o => o.GetProperty("transactionId").GetString()),
            result.Obligations!.Select(o => o.TransactionId));
        foreach (var (expectedObligation, actual) in expectedObligations.Zip(result.Obligations!))
        {
            Assert.Equal(expectedObligation.GetProperty("originalAmount").GetDecimal(), actual.OriginalAmount);
            Assert.Equal(expectedObligation.GetProperty("remainingAmount").GetDecimal(), actual.RemainingAmount);
            if (expectedObligation.TryGetProperty("date", out var expectedDate)
                && expectedDate.ValueKind != JsonValueKind.Null)
            {
                Assert.Equal(DateOnly.Parse(expectedDate.GetString()!), actual.Date);
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "stability-reload.cases.json");
}
