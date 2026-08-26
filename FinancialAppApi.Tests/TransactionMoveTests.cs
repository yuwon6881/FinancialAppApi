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

    [Fact]
    public async Task MoveTransactionsAsync_LeavesEachSplitChildsAccountPlacementAlone()
    {
        // The client re-derives an income parent's children when it projects a move, so it has to
        // reach the same answer this does: only the date changes. The frontend counterpart is
        // "re-deriving children keeps their account placement" in incomeSplitProjection.test.ts.
        await using var context = TestHelpers.NewInMemoryContext();
        var parent = NewTransaction("tx-income", "Salary", "Salary", "Income", 1000m, new DateOnly(2026, 8, 1));
        var child = NewTransaction("tx-income-split-Growth", "[Split: Growth] Salary", "Transfer", "Transfer:Income->Growth", 250m, new DateOnly(2026, 8, 1));
        child.AccountId = "acct-growth";
        context.Transactions.AddRange(parent, child);
        await context.SaveChangesAsync();

        var result = await NewService(context).MoveTransactionsAsync(
            [new TransactionMoveRequest("tx-income", "2026-09-03")]);

        Assert.Equal(TransactionMutationStatus.Updated, result.Status);
        var moved = context.Transactions.Single(transaction => transaction.Id == "tx-income-split-Growth");
        Assert.Equal(new DateOnly(2026, 9, 3), DateOnly.FromDateTime(moved.Date));
        Assert.Equal("acct-growth", moved.AccountId);
        Assert.Equal(250m, moved.Amount);
    }

    [Fact]
    public async Task MoveTransactionsAsync_ReadsTheStoredDateEvenWhenAnEarlierAttemptAlreadyMutatedIt()
    {
        // Stands in for an execution-strategy retry: the context is shared across attempts, so a
        // half-applied first attempt leaves the entity tracked with its new Date. If the re-read
        // is served by identity resolution, the source date is lost and only the destination
        // cycle gets its CycleBalance invalidated.
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.Add(
            NewTransaction("tx-plain", "Lunch", "Food", "Essentials", -10m, new DateOnly(2026, 6, 15)));
        await context.SaveChangesAsync();

        var tracked = context.Transactions.Single(transaction => transaction.Id == "tx-plain");
        tracked.Date = TransactionDate.FromInputDate(new DateOnly(2026, 8, 1));

        var result = await NewService(context).MoveTransactionsAsync(
            [new TransactionMoveRequest("tx-plain", "2026-08-01")]);

        Assert.Equal(TransactionMutationStatus.Updated, result.Status);
        Assert.Contains("2026-06-15", result.AffectedDates!);
        Assert.Contains("2026-08-01", result.AffectedDates!);
    }

    [Fact]
    public async Task MoveTransactionsAsync_ReportsBothTheSourceAndDestinationDates()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.Add(
            NewTransaction("tx-plain", "Lunch", "Food", "Essentials", -10m, new DateOnly(2026, 8, 1)));
        await context.SaveChangesAsync();

        var result = await NewService(context).MoveTransactionsAsync(
            [new TransactionMoveRequest("tx-plain", "2026-06-15")]);

        Assert.Equal(TransactionMutationStatus.Updated, result.Status);
        Assert.Equal(["2026-06-15", "2026-08-01"], result.AffectedDates!);
    }

    [Theory]
    [InlineData("savings-goal")]
    [InlineData("wishlist")]
    [InlineData("adjustment")]
    [InlineData("stability-top-up")]
    public async Task MoveTransactionsAsync_RejectsEveryProtectedRowShape(string shape)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var transaction = NewTransaction("tx-protected", "Record", "Other", "Rewards", -10m, new DateOnly(2026, 8, 1));
        switch (shape)
        {
            case "savings-goal": transaction.SavingsGoalId = 7; break;
            case "wishlist": transaction.WishlistItemId = 7; break;
            case "adjustment": transaction.IsAccountBalanceAdjustment = true; break;
            case "stability-top-up": transaction.StabilityRecoveryTopUpAmount = 25m; break;
        }
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var result = await NewService(context).MoveTransactionsAsync(
            [new TransactionMoveRequest("tx-protected", "2026-09-03")]);

        Assert.Equal(TransactionMutationStatus.Conflict, result.Status);
        Assert.Equal(new DateOnly(2026, 8, 1), DateOnly.FromDateTime(context.Transactions.Single().Date));
    }

    [Fact]
    public async Task MoveTransactionsAsync_RejectsATransactionOwnedByAnotherUser()
    {
        // Seeded as the other user, because the write-time ownership check refuses to save a row
        // for anyone but the current user.
        await using var context = TestHelpers.NewInMemoryContext("someone-else");
        var foreign = NewTransaction("tx-foreign", "Lunch", "Food", "Essentials", -10m, new DateOnly(2026, 8, 1));
        foreign.UserId = "someone-else";
        context.Transactions.Add(foreign);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        context.SetCurrentUser(TestHelpers.DefaultUserId);

        var result = await NewService(context).MoveTransactionsAsync(
            [new TransactionMoveRequest("tx-foreign", "2026-09-03")]);

        Assert.Equal(TransactionMutationStatus.Conflict, result.Status);
        Assert.Contains("no longer exist", result.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task MoveTransactionsAsync_RejectsABatchOutsideTheSupportedSize(int count)
    {
        await using var context = TestHelpers.NewInMemoryContext();

        var result = await NewService(context).MoveTransactionsAsync(
            Enumerable.Range(1, count)
                .Select(index => new TransactionMoveRequest($"tx-{index}", "2026-09-03"))
                .ToArray());

        Assert.Equal(TransactionMutationStatus.Conflict, result.Status);
        Assert.Contains("1 and 100", result.Message);
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
