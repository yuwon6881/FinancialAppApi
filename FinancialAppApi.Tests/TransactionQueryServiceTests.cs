using System.Text;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public class TransactionQueryServiceTests
{
    [Fact]
    public async Task GetTransactionsAsync_AllModeReturnsPagedFilteredResults()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("tx-1", "Coffee", "Food", "Rewards", -10m),
            NewTransaction("tx-2", "Salary", "Salary", "Income", 1000m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, page: 1, pageSize: 10, txType: "outflow");

        Assert.Equal(1, result.Total);
        Assert.Single(result.Items);
        Assert.Equal("tx-1", result.Items[0].Id);
    }

    [Fact]
    public async Task GetTransactionsAsync_OutflowFilterExcludesTransfersEvenIfAmountIsNegative()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("expense", "Coffee", "Food", "Rewards", -10m),
            NewTransaction("transfer", "Legacy transfer", "Transfer", "Transfer:Rewards->Growth", -10m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, txType: "outflow");

        Assert.Equal("expense", Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task GetTransactionsAsync_AppliesDateAmountAndRecurringFiltersTogether()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("matching", "Rent", "Housing", "Essentials", -1250m,
                date: new DateOnly(2026, 7, 10), recurringPaymentId: "rent-plan"),
            NewTransaction("too-small", "Streaming", "Entertainment", "Rewards", -20m,
                date: new DateOnly(2026, 7, 10), recurringPaymentId: "streaming-plan"),
            NewTransaction("not-recurring", "Furniture", "Home", "Essentials", -1250m,
                date: new DateOnly(2026, 7, 10)),
            NewTransaction("outside-range", "Old rent", "Housing", "Essentials", -1250m,
                date: new DateOnly(2026, 6, 10), recurringPaymentId: "rent-plan"));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(
            all: true,
            startDate: "2026-07-01",
            endDate: "2026-07-31",
            minAmount: 100m,
            maxAmount: 1500m,
            recurringOnly: true);

        Assert.Equal("matching", Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task GetTransactionsAsync_WishlistOnlyReturnsLinkedPurchases()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("wishlist", "Purchased: Headphones", "Other", "Rewards", -80m, wishlistItemId: 12),
            NewTransaction("ordinary", "Coffee", "Food", "Rewards", -10m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, wishlistOnly: true);

        var transaction = Assert.Single(result.Items);
        Assert.Equal("wishlist", transaction.Id);
        Assert.Equal(12, transaction.WishlistItemId);
    }

    [Fact]
    public async Task GetTransactionsAsync_OrdersSameDayRowsByCreationTimestamp()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("older", "AI entry", "Other", "Essentials", -10m,
                postedAt: new DateTime(2026, 7, 9, 1, 0, 0, DateTimeKind.Utc)),
            NewTransaction("newer", "Manual entry", "Other", "Essentials", -20m,
                postedAt: new DateTime(2026, 7, 9, 15, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true);

        Assert.Equal(["newer", "older"], result.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task GetTransactionsAsync_SearchMatchesDescriptionCategoryAndLedgerCaseInsensitively()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("by-description", "Grocery Store", "Food", "Essentials", -10m),
            NewTransaction("by-category", "Weekly shop", "GROCERIES", "Essentials", -12m),
            NewTransaction("no-match", "Rent", "Housing", "Essentials", -1000m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, search: "grocer");

        Assert.Equal(
            new[] { "by-category", "by-description" },
            result.Items.Select(item => item.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task ExportTransactionsAsync_IncludesCsvHeaderAndRows()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("tx-1", "Coffee", "Food", "Rewards", -10m),
            NewTransaction("tx-2", "Move", "Transfer", "Transfer:Rewards->Growth", 25m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.ExportTransactionsAsync();

        var csv = Encoding.UTF8.GetString(result.Bytes);
        Assert.Contains("Date,Description,Category,Ledger Allocation,Debit (Outflow),Credit (Inflow),Internal Movement", csv);
        Assert.Contains("Coffee", csv);
        Assert.Contains("Move,Transfer,Rewards -> Growth,,,25.00", csv);
    }

    [Fact]
    public async Task GetAutocompleteSuggestionsAsync_ExcludesGeneratedIncomeSplits()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("income-1", "Salary", "Salary", "Income", 1000m),
            NewTransaction("tx-1780023496780-split-Essentials", "[Split: Stability] Jun Salary", "Salary", "Essentials", 400m),
            NewTransaction("generated-transfer-row", "[Split: Growth] Jun Salary", "Salary", "Transfer:Income->Growth", 300m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var suggestions = await service.GetAutocompleteSuggestionsAsync();

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("Salary", suggestion.Description);
        Assert.Equal("Income", suggestion.LedgerCategory);
    }

    [Fact]
    public async Task GetAutocompleteSuggestionsAsync_ExcludesWishlistPurchases()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("ordinary-1", "Prawn Noodle Soup", "Food", "Essentials", -10m),
            NewTransaction("wishlist-1", "Purchased: Rave (Wish List)", "Other", "Rewards", -200m, wishlistItemId: 42));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var suggestions = await service.GetAutocompleteSuggestionsAsync();

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("Prawn Noodle Soup", suggestion.Description);
    }

    private static Transaction NewTransaction(
        string id,
        string description,
        string category,
        string ledgerCategory,
        decimal amount,
        DateTime? postedAt = null,
        DateOnly? date = null,
        string? recurringPaymentId = null,
        int? wishlistItemId = null)
    {
        return new Transaction
        {
            Id = id,
            Date = TransactionDate.FromInputDate(date ?? new DateOnly(2026, 7, 9)),
            PostedAt = postedAt ?? new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc),
            Description = description,
            Category = category,
            LedgerCategory = ledgerCategory,
            Amount = amount,
            RecurringPaymentId = recurringPaymentId,
            WishlistItemId = wishlistItemId
        };
    }
}
