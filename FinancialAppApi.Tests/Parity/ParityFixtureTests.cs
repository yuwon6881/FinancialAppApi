using System.Text.Json;
using FinancialAppApi.Models;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests.Parity;

public sealed class ParityFixtureTests
{
    public static IEnumerable<object[]> BucketAttributionCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = item.GetProperty("input");
            var transaction = input.GetProperty("transaction");
            yield return
            [
                item.GetProperty("id").GetString()!,
                item.GetProperty("why").GetString()!,
                new Transaction
                {
                    Amount = transaction.GetProperty("amount").GetDecimal(),
                    LedgerCategory = transaction.GetProperty("ledgerCategory").ValueKind == JsonValueKind.Null
                        ? string.Empty
                        : transaction.GetProperty("ledgerCategory").GetString() ?? string.Empty,
                    Category = transaction.GetProperty("category").ValueKind == JsonValueKind.Null
                        ? string.Empty
                        : transaction.GetProperty("category").GetString() ?? string.Empty,
                },
                input.GetProperty("bucket").GetString()!,
                item.GetProperty("expected").GetDecimal(),
            ];
        }
    }

    [Theory]
    [MemberData(nameof(BucketAttributionCases))]
    public void BucketAttributionMatchesSharedFixture(
        string id,
        string why,
        Transaction transaction,
        string bucket,
        decimal expected)
    {
        var actual = CategoryAttributionService.GetCategoryAmount(transaction, bucket);

        Assert.True(actual == expected, $"{id}: {why}; expected {expected}, got {actual}");
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Parity",
        "Fixtures",
        "bucket-attribution.cases.json");
}
