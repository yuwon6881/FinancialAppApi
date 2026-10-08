using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Stability;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public sealed class CardSettlementSyncTests
{
    [Fact]
    public async Task ARejectedPaymentStopsTheBatchBeforeItsRebate()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.LedgerAccounts.AddRange(
            new LedgerAccount { Id = "bank", Name = "Bank", Bucket = "Essentials", Kind = LedgerAccountKind.Bank, IsArchived = true },
            new LedgerAccount { Id = "visa", Name = "Visa", Bucket = "Essentials", Kind = LedgerAccountKind.CreditCard });
        context.TransactionCategories.Add(new TransactionCategory { Id = "cashback", Name = "Cashback" });
        await context.SaveChangesAsync();
        var cycles = new CycleBalanceService(context);
        var service = new TransactionPersistenceService(context, cycles,
            new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance),
            new StabilityRecoveryService(context, cycles));
        var payment = new TransactionMutationRequest("payment", "2026-10-08", null, "Pay Visa", "Transfer", "AccountMove",
            ObfuscationHelper.Obfuscate(250m), null, null, AccountId: "bank", CounterAccountId: "visa");
        var rebate = new TransactionMutationRequest("rebate", "2026-10-08", null, "Rebate", "Cashback", "Essentials",
            ObfuscationHelper.Obfuscate(30m), null, null, AccountId: "visa");

        var result = await service.CreateTransactionsAsync([payment, rebate]);

        Assert.Equal(TransactionMutationStatus.InvalidAccount, result.Status);
        Assert.Equal(0, result.FailedIndex);
        Assert.Empty(context.Transactions);
    }
}
