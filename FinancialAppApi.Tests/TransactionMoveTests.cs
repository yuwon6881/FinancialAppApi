using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public class TransactionMoveTests
{
    [Fact]
    public async Task MoveTransactionsAsync_CarriesIncomeSplitRowsToTheSameDate()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("tx-income", "Salary", "Salary", "Income", 1000m, new DateOnly(2026, 8, 1)),
            NewTransaction("tx-income-split-Essentials", "Essentials", "Salary", "IncomeSplit:Essentials", 500m, new DateOnly(2026, 8, 1)),
            NewTransaction("tx-other", "Lunch", "Food", "Essentials", -10m, new DateOnly(2026, 8, 1)));
        await context.SaveChangesAsync();

        var result = await NewService(context).MoveTransactionsAsync(
            [new TransactionMoveRequest("tx-income", "2026-09-03")]);

        Assert.Equal(TransactionMutationStatus.Updated, result.Status);
        Assert.Equal(new DateOnly(2026, 9, 3), DateOnly.FromDateTime(context.Transactions.Single(t => t.Id == "tx-income").Date));
        Assert.Equal(new DateOnly(2026, 9, 3), DateOnly.FromDateTime(context.Transactions.Single(t => t.Id == "tx-income-split-Essentials").Date));
        Assert.Equal(new DateOnly(2026, 8, 1), DateOnly.FromDateTime(context.Transactions.Single(t => t.Id == "tx-other").Date));
    }

    [Fact]
    public async Task MoveTransactionsAsync_RejectsASplitRowAsAMoveTarget()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.Add(
            NewTransaction("tx-income-split-Essentials", "Essentials", "Salary", "IncomeSplit:Essentials", 500m, new DateOnly(2026, 8, 1)));
        await context.SaveChangesAsync();

        var result = await NewService(context).MoveTransactionsAsync(
            [new TransactionMoveRequest("tx-income-split-Essentials", "2026-09-03")]);

        Assert.Equal(TransactionMutationStatus.Conflict, result.Status);
        Assert.Equal(new DateOnly(2026, 8, 1), DateOnly.FromDateTime(context.Transactions.Single().Date));
    }

    [Fact]
    public async Task MoveTransactionsAsync_MovesNothingWhenOneRowInTheBatchIsIneligible()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var settlement = NewTransaction("tx-bill", "Rent", "Bills", "Essentials", -900m, new DateOnly(2026, 8, 1));
        settlement.RecurringPaymentId = "rec-1";
        context.Transactions.AddRange(
            NewTransaction("tx-plain", "Lunch", "Food", "Essentials", -10m, new DateOnly(2026, 8, 1)),
            settlement);
        await context.SaveChangesAsync();

        var result = await NewService(context).MoveTransactionsAsync([
            new TransactionMoveRequest("tx-plain", "2026-09-03"),
            new TransactionMoveRequest("tx-bill", "2026-09-03"),
        ]);

        Assert.Equal(TransactionMutationStatus.Conflict, result.Status);
        Assert.All(context.Transactions, transaction =>
            Assert.Equal(new DateOnly(2026, 8, 1), DateOnly.FromDateTime(transaction.Date)));
    }

    [Fact]
    public async Task MoveTransactionsAsync_RejectsARepeatedTransactionInOneRequest()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.Add(NewTransaction("tx-plain", "Lunch", "Food", "Essentials", -10m, new DateOnly(2026, 8, 1)));
        await context.SaveChangesAsync();

        var result = await NewService(context).MoveTransactionsAsync([
            new TransactionMoveRequest("tx-plain", "2026-09-03"),
            new TransactionMoveRequest("tx-plain", "2026-09-04"),
        ]);

        Assert.Equal(TransactionMutationStatus.Conflict, result.Status);
    }

    private static TransactionPersistenceService NewService(AppDbContext context)
    {
        var cycleBalanceService = new CycleBalanceService(context);
        return new TransactionPersistenceService(
            context,
            cycleBalanceService,
            new RecurringOccurrenceService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RecurringOccurrenceService>.Instance),
            new Services.Stability.StabilityRecoveryService(context, cycleBalanceService));
    }

    private static Transaction NewTransaction(
        string id,
        string description,
        string category,
        string ledgerCategory,
        decimal amount,
        DateOnly date) => new()
        {
            Id = id,
            UserId = TestHelpers.DefaultUserId,
            Date = TransactionDate.FromInputDate(date),
            PostedAt = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Utc),
            Description = description,
            Category = category,
            LedgerCategory = ledgerCategory,
            Amount = amount,
        };
}
