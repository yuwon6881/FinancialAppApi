using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class TransactionPersistenceServiceTests
{
    [Fact]
    public async Task CreateTransactionAsync_CreatesIncomeSplitsFromSettings()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.FinancialSettings.Add(new FinancialSetting
        {
            EssentialsAlloc = 0.50m,
            GrowthAlloc = 0.25m,
            StabilityAlloc = 0.15m,
            RewardsAlloc = 0.10m,
            CycleDay = 1
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateTransactionAsync(NewRequest("tx-1", ledgerCategory: "Income", amount: 1000m));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal(5, await context.Transactions.CountAsync());
        Assert.True(await context.Transactions.AnyAsync(t => t.Id == "tx-1-split-Essentials" && t.Amount == 500m));
    }

    [Fact]
    public async Task CreateTransactionAsync_IncomeSplitsReconcileExactlyToSalaryAfterRounding()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.FinancialSettings.Add(new FinancialSetting
        {
            EssentialsAlloc = 0.50m,
            GrowthAlloc = 0.25m,
            StabilityAlloc = 0.15m,
            RewardsAlloc = 0.10m,
            CycleDay = 1
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateTransactionAsync(
            NewRequest("salary-with-cents", ledgerCategory: "Income", amount: 1000.05m));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        var splits = await context.Transactions
            .Where(t => t.Id.StartsWith("salary-with-cents-split-"))
            .ToListAsync();
        Assert.Equal(4, splits.Count);
        Assert.Equal(1000.05m, splits.Sum(t => t.Amount));
    }

    [Fact]
    public async Task CreateTransactionAsync_ReturnsExistingForIdempotentPost()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.Transactions.Add(NewTransaction("tx-1"));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateTransactionAsync(NewRequest("tx-1"));

        Assert.Equal(TransactionMutationStatus.Existing, result.Status);
        Assert.Equal("tx-1", result.Transaction!.Id);
    }

    [Fact]
    public async Task CreateTransactionAsync_RejectsMalformedObfuscatedAmount()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = NewService(context);
        var request = new TransactionMutationRequest(
            "tx-invalid",
            "2026-07-09",
            null,
            "Invalid amount",
            "Other",
            "Rewards",
            "not-base64-or-a-number",
            null,
            null);

        var result = await service.CreateTransactionAsync(request);

        Assert.Equal(TransactionMutationStatus.InvalidAmount, result.Status);
        Assert.Empty(context.Transactions);
    }

    [Fact]
    public async Task CreateTransactionAsync_SeparatesCalendarDateFromClientCreationTimestamp()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = NewService(context);
        var postedAt = "2026-07-09T15:00:00.123Z";

        var result = await service.CreateTransactionAsync(NewRequest("tx-timestamp", postedAt: postedAt));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal(new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc), result.Transaction!.Date);
        Assert.Equal(DateTime.Parse(postedAt).ToUniversalTime(), result.Transaction.PostedAt);
    }

    [Theory]
    [InlineData("Transfer:Rewards->Rewards", 25, "Transfer source and target must be different.")]
    [InlineData("Transfer:Rewards->Unknown", 25, "Transfer source and target must be one of")]
    [InlineData("Transfer:Rewards", 25, "must use the format")]
    [InlineData("Transfer:Rewards->Growth", -25, "Transfer amount must be greater than zero.")]
    public async Task CreateTransactionAsync_RejectsInvalidTransferRoutes(
        string ledgerCategory,
        decimal amount,
        string expectedMessage)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateTransactionAsync(
            NewRequest("tx-transfer", ledgerCategory, amount, "Transfer"));

        Assert.Equal(TransactionMutationStatus.InvalidLedgerCategory, result.Status);
        Assert.Contains(expectedMessage, result.Message);
        Assert.Empty(context.Transactions);
    }

    [Fact]
    public async Task CreateTransactionAsync_CanonicalizesValidTransferRoute()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateTransactionAsync(
            NewRequest("tx-transfer", " transfer: rewards -> growth ", 25m, "transfer"));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal("Transfer", result.Transaction!.Category);
        Assert.Equal("Transfer:Rewards->Growth", result.Transaction.LedgerCategory);
    }

    [Fact]
    public async Task CreateTransactionAsync_RejectsTransferCategoryWithoutRoute()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateTransactionAsync(
            NewRequest("tx-transfer", "Rewards", 25m, "Transfer"));

        Assert.Equal(TransactionMutationStatus.InvalidLedgerCategory, result.Status);
        Assert.Contains("requires a valid", result.Message);
    }

    [Fact]
    public async Task UpdateTransactionAsync_ReplacesExistingSplits()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.Transactions.AddRange(
            NewTransaction("tx-1", ledgerCategory: "IncomeSplit:50,25,15,10", amount: 1000m),
            NewTransaction("tx-1-split-Essentials", ledgerCategory: "Transfer:Income->Essentials", amount: 500m));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.UpdateTransactionAsync(
            "tx-1",
            NewRequest("tx-1", ledgerCategory: "IncomeSplit:25,25,25,25", amount: 800m));

        Assert.Equal(TransactionMutationStatus.Updated, result.Status);
        Assert.Equal(5, await context.Transactions.CountAsync());
        Assert.True(await context.Transactions.AnyAsync(t => t.Id == "tx-1-split-Rewards" && t.Amount == 200m));
    }

    [Fact]
    public async Task DeleteTransactionAsync_ClearsWishlistPurchaseLinkAndDeletesSplits()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.WishlistItems.Add(new WishlistItem
        {
            Id = 1,
            Name = "Camera",
            Price = 100m,
            Priority = "Medium",
            IsPurchased = true,
            PurchaseTransactionId = "tx-1"
        });
        context.Transactions.AddRange(
            NewTransaction("tx-1", wishlistItemId: 1),
            NewTransaction("tx-1-split-Rewards", ledgerCategory: "Transfer:Income->Rewards", amount: 10m));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.DeleteTransactionAsync("tx-1");

        Assert.Equal(TransactionMutationStatus.Deleted, result.Status);
        Assert.Empty(await context.Transactions.ToListAsync());
        var item = (await context.WishlistItems.FindAsync(1))!;
        Assert.False(item.IsPurchased);
        Assert.True(item.IsActive);
        Assert.Null(item.PurchaseTransactionId);
    }

    private static TransactionPersistenceService NewService(AppDbContext context)
    {
        var occurrenceService = new RecurringOccurrenceService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RecurringOccurrenceService>.Instance);
        return new TransactionPersistenceService(context, new CycleBalanceService(context), occurrenceService);
    }

    private static void SeedCategories(AppDbContext context)
    {
        context.TransactionCategories.AddRange(
            new TransactionCategory { Id = "cat-other", Name = "Other" },
            new TransactionCategory { Id = "cat-transfer", Name = "Transfer" });
    }

    private static TransactionMutationRequest NewRequest(
        string id,
        string ledgerCategory = "Rewards",
        decimal amount = -25m,
        string category = "Other",
        string? postedAt = null)
    {
        return new TransactionMutationRequest(
            id,
            "2026-07-09",
            postedAt,
            "Test transaction",
            category,
            ledgerCategory,
            ObfuscationHelper.Obfuscate(amount),
            null,
            null);
    }

    private static Transaction NewTransaction(
        string id,
        string ledgerCategory = "Rewards",
        decimal amount = -25m,
        int? wishlistItemId = null)
    {
        return new Transaction
        {
            Id = id,
            Date = DateTime.SpecifyKind(new DateTime(2026, 7, 9), DateTimeKind.Utc),
            Description = "Test transaction",
            Category = "Other",
            LedgerCategory = ledgerCategory,
            Amount = amount,
            WishlistItemId = wishlistItemId
        };
    }
}
