using System.Text.Json;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Tests.Parity;

public sealed class AccountAttributionParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var why = item.GetProperty("why").GetString()!;
            var input = item.GetProperty("input");
            var txJson = input.GetProperty("transaction");
            var acctJson = input.GetProperty("account");
            var accountsJson = input.GetProperty("accounts");

            var tx = new Transaction
            {
                Amount = txJson.GetProperty("amount").GetDecimal(),
                LedgerCategory = txJson.GetProperty("ledgerCategory").GetString() ?? string.Empty,
                AccountId = txJson.GetProperty("accountId").ValueKind == JsonValueKind.Null ? null : txJson.GetProperty("accountId").GetString(),
                CounterAccountId = txJson.GetProperty("counterAccountId").ValueKind == JsonValueKind.Null ? null : txJson.GetProperty("counterAccountId").GetString(),
            };

            var account = new LedgerAccount
            {
                Id = acctJson.GetProperty("id").GetString()!,
                Bucket = acctJson.GetProperty("bucket").GetString()!,
            };

            var accounts = accountsJson.EnumerateArray().Select(a => new LedgerAccount
            {
                Id = a.GetProperty("id").GetString()!,
                Bucket = a.GetProperty("bucket").GetString()!,
            }).ToDictionary(a => a.Id, a => a);

            var expected = item.GetProperty("expected").GetDecimal();
            yield return [id, why, tx, account, accounts, expected];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AccountAttributionMatchesCanonicalFixture(
        string id,
        string why,
        Transaction transaction,
        LedgerAccount account,
        Dictionary<string, LedgerAccount> accounts,
        decimal expected)
    {
        var actual = LedgerAccountAttribution.GetAccountAmount(transaction, account, accounts);
        Assert.True(actual == expected, $"{id}: {why}; expected {expected}, got {actual}");
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "account-attribution.cases.json");
}
