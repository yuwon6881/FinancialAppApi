using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Stability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests.Parity;

public sealed class IncomeSplitProjectionParityTests
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
    public async Task IncomeSplitProjectionMatchesCanonicalFixture(JsonElement item)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var cycleBalanceService = new CycleBalanceService(context);
        var occurrenceService = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        var recoveryService = new StabilityRecoveryService(context, cycleBalanceService);
        var service = new TransactionPersistenceService(
            context,
            cycleBalanceService,
            occurrenceService,
            recoveryService);

        context.TransactionCategories.Add(new TransactionCategory { Id = "cat-salary", Name = "Salary" });

        var input = item.GetProperty("input");
        var txJson = input.GetProperty("transaction");
        var txId = txJson.GetProperty("id").GetString()!;
        var amount = txJson.GetProperty("amount").GetDecimal();
        var ledgerCategory = txJson.GetProperty("ledgerCategory").GetString()!;
        var description = txJson.GetProperty("description").GetString()!;
        var date = txJson.GetProperty("date").GetString()!;
        var postedAt = txJson.GetProperty("postedAt").GetString();

        var splitAccounts = new Dictionary<string, string>();
        if (txJson.TryGetProperty("splitAccountIds", out var splitAcctsJson) && splitAcctsJson.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in splitAcctsJson.EnumerateObject())
            {
                splitAccounts[prop.Name] = prop.Value.GetString()!;
                if (!context.LedgerAccounts.Any(a => a.Id == prop.Value.GetString()))
                {
                    context.LedgerAccounts.Add(new LedgerAccount
                    {
                        Id = prop.Value.GetString()!,
                        Name = prop.Value.GetString()!,
                        Bucket = prop.Name,
                        Kind = LedgerAccountKind.Bank,
                        UserId = TestHelpers.DefaultUserId,
                    });
                }
            }
            await context.SaveChangesAsync();
        }

        var request = new TransactionMutationRequest(
            Id: txId,
            Date: date,
            PostedAt: postedAt,
            Description: description,
            Category: "Salary",
            LedgerCategory: ledgerCategory,
            Amount: ObfuscationHelper.Obfuscate(amount),
            RecurringPaymentId: null,
            WishlistItemId: null,
            SplitAccountIds: splitAccounts.Count > 0 ? splitAccounts : null);

        var result = await service.CreateTransactionAsync(request, CancellationToken.None);

        var expectedArray = item.GetProperty("expected").EnumerateArray().ToList();
        var generatedSplits = await context.Transactions
            .Where(t => t.Id.StartsWith($"{txId}-split-"))
            .OrderBy(t => t.Id)
            .ToListAsync();

        Assert.Equal(expectedArray.Count, generatedSplits.Count);

        for (var i = 0; i < expectedArray.Count; i++)
        {
            var exp = expectedArray[i];
            var expId = exp.GetProperty("id").GetString();
            var act = generatedSplits.First(s => s.Id == expId);

            Assert.Equal(exp.GetProperty("description").GetString(), act.Description);
            Assert.Equal(exp.GetProperty("category").GetString(), act.Category);
            Assert.Equal(exp.GetProperty("ledgerCategory").GetString(), act.LedgerCategory);
            Assert.Equal(exp.GetProperty("amount").GetDecimal(), act.Amount);
            Assert.Equal(exp.GetProperty("accountId").GetString(), act.AccountId);
        }
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "income-split-projection.cases.json");
}
