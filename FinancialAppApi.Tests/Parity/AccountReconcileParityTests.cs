using System.Text.Json;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Tests.Parity;

public sealed class AccountReconcileParityTests
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
    public async Task AccountReconcileMatchesCanonicalFixture(JsonElement item)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = new LedgerAccountService(
            context,
            new LedgerAccountBalanceService(context),
            new CycleBalanceService(context));

        var input = item.GetProperty("input");
        var operationId = input.GetProperty("operationId").GetString()!;
        var bucket = input.GetProperty("bucket").GetString()!;
        var expectedBucketTotal = input.GetProperty("expectedBucketTotal").GetDecimal();
        var description = input.TryGetProperty("description", out var descriptionProperty)
            ? descriptionProperty.GetString()
            : null;

        var targets = new List<LedgerAccountReconcileTarget>();
        foreach (var target in input.GetProperty("targets").EnumerateArray())
        {
            var id = target.GetProperty("id").GetString()!;
            var name = target.GetProperty("name").GetString()!;
            var expectedCurrent = target.GetProperty("expectedCurrent").GetDecimal();
            var targetAmount = target.GetProperty("target").GetDecimal();

            var account = new LedgerAccount
            {
                Id = id,
                Name = name,
                Bucket = bucket,
                Kind = LedgerAccountKind.Bank,
                UserId = TestHelpers.DefaultUserId,
            };
            context.LedgerAccounts.Add(account);

            if (expectedCurrent != 0m)
            {
                context.Transactions.Add(new Transaction
                {
                    Id = $"init-{id}",
                    UserId = TestHelpers.DefaultUserId,
                    Date = DateTime.UtcNow,
                    PostedAt = DateTime.UtcNow,
                    Description = "Opening balance",
                    Category = "Adjustment",
                    LedgerCategory = bucket,
                    Amount = expectedCurrent,
                    AccountId = id,
                });
            }

            targets.Add(new LedgerAccountReconcileTarget(
                Id: id,
                Name: name,
                Kind: LedgerAccountKind.Bank,
                ExpectedCurrent: expectedCurrent,
                Target: targetAmount,
                IsArchived: false));
        }

        await context.SaveChangesAsync();

        var request = new LedgerAccountReconcileRequest(
            operationId,
            bucket,
            expectedBucketTotal,
            AdjustmentAccountId: null,
            Targets: targets,
            Description: description);

        var result = await service.ReconcileAsync(request, CancellationToken.None);

        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        Assert.NotNull(result.Transactions);

        var expectedArray = item.GetProperty("expected").EnumerateArray().ToList();
        Assert.Equal(expectedArray.Count, result.Transactions.Count);

        for (var i = 0; i < expectedArray.Count; i++)
        {
            var exp = expectedArray[i];
            var act = result.Transactions[i];
            Assert.Equal(exp.GetProperty("id").GetString(), act.Id);
            Assert.Equal(exp.GetProperty("category").GetString(), act.Category);
            Assert.Equal(exp.GetProperty("ledgerCategory").GetString(), act.LedgerCategory);
            Assert.Equal(exp.GetProperty("amount").GetDecimal(), act.Amount);
            Assert.Equal(exp.GetProperty("accountId").GetString(), act.AccountId);
            if (exp.TryGetProperty("isAccountBalanceAdjustment", out var expBalanceAdjustment))
            {
                Assert.Equal(expBalanceAdjustment.GetBoolean(), act.IsAccountBalanceAdjustment);
            }
            if (exp.TryGetProperty("counterAccountId", out var expCounter) && expCounter.ValueKind != JsonValueKind.Null)
            {
                Assert.Equal(expCounter.GetString(), act.CounterAccountId);
            }
        }
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "account-reconcile.cases.json");
}
