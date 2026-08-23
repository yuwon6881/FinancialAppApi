using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public class TransactionQueryServiceTests
{
    [Fact]
    public void ProjectCycleTransactions_MatchesCycleFilteringAndCanonicalOrdering()
    {
        var transactions = new List<Transaction>
        {
            NewTransaction("older", "Older", "Food", "Essentials", -1m,
                postedAt: new DateTime(2026, 7, 3, 8, 0, 0, DateTimeKind.Utc), date: new DateOnly(2026, 7, 3)),
            NewTransaction("newer", "Newer", "Food", "Essentials", -2m,
                postedAt: new DateTime(2026, 7, 3, 9, 0, 0, DateTimeKind.Utc), date: new DateOnly(2026, 7, 3)),
            NewTransaction("discarded", "Discarded", "Other", "Discarded", 0m,
                date: new DateOnly(2026, 7, 4)),
            NewTransaction("outside", "Outside", "Food", "Essentials", -3m,
                date: new DateOnly(2026, 8, 3)),
        };

        var projected = TransactionQueryService.ProjectCycleTransactions(
            transactions, 2026, 7, cycleDay: 1);

        Assert.Equal(["newer", "older"], projected.Items.Select(item => item.Id));
    }

    [Fact]
    public void ProjectCycleTransactions_PreservesAccountPlacement()
    {
        var transaction = NewTransaction("ordinary", "Groceries", "Food", "Essentials", -40m);
        transaction.AccountId = "essentials-wallet";
        transaction.CounterAccountId = "rewards-card";

        var projected = TransactionQueryService.ProjectCycleTransactions(
            [transaction], 2026, 7, cycleDay: 1);

        var item = Assert.Single(projected.Items);
        Assert.Equal("essentials-wallet", item.AccountId);
        Assert.Equal("rewards-card", item.CounterAccountId);
    }

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
    public async Task GetTransactionsAsync_WholeWordModeUsesLiteralUnicodeBoundaries()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("match", "Iced Coffee Bean Latte", "Food", "Rewards", -10m),
            NewTransaction("plural", "Coffee Beans", "Food", "Rewards", -10m),
            NewTransaction("literal", "Cafe (special)", "Food", "Rewards", -10m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var phrase = await service.GetTransactionsAsync(all: true, search: "coffee bean", searchMode: "whole-word");
        var literal = await service.GetTransactionsAsync(all: true, search: "cafe (special)", searchMode: "whole-word");

        Assert.Equal("match", Assert.Single(phrase.Items).Id);
        Assert.Equal("literal", Assert.Single(literal.Items).Id);
    }

    [Fact]
    public async Task GetTransactionsAsync_OutflowFilterExcludesTransfersAndAdjustmentsCaseInsensitively()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("expense", "Coffee", "Food", "Rewards", -10m),
            NewTransaction("transfer", "Legacy transfer", "transfer", "transfer:Rewards->Growth", -10m),
            NewTransaction("adjustment", "Balance correction", "ADJUSTMENT", "Rewards", -20m));
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
    public async Task GetTransactionsAsync_CountsTheUnderlyingRecurringRowOnceWhenALoanIsLinked()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Loans.Add(new Loan
        {
            Id = "loan-report",
            Name = "Loan report test",
            RecurringPaymentId = "loan-bill",
            OpeningPrincipal = 1000m,
            TrackingStartDate = new DateOnly(2026, 7, 1),
            AnnualRatePercent = 0m,
            TermPeriods = 10,
            InterestMethod = LoanInterestMethod.ReducingBalance,
            ScheduleFrequency = "Monthly",
            ScheduleDueDay = 1,
            ScheduleStartDate = new DateOnly(2026, 1, 1),
            ScheduleStatus = LoanScheduleStatus.Complete,
        });
        context.Transactions.Add(NewTransaction(
            "loan-report-tx",
            "Loan bill",
            "Bills",
            "Essentials",
            -100m,
            recurringPaymentId: "loan-bill"));
        await context.SaveChangesAsync();

        var result = await new TransactionQueryService(context).GetTransactionsAsync(all: true);

        Assert.Equal(1, result.Total);
        Assert.Equal("loan-report-tx", Assert.Single(result.Items).Id);
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
    public async Task GetTransactionsAsync_SupportsExcludingRecurringAndWishlistLinks()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("recurring", "Rent", "Housing", "Essentials", -100m, recurringPaymentId: "rent-plan"),
            NewTransaction("wishlist", "Purchased: Headphones", "Other", "Rewards", -80m, wishlistItemId: 12),
            NewTransaction("ordinary", "Coffee", "Food", "Rewards", -10m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var withoutRecurring = await service.GetTransactionsAsync(all: true, recurringFilter: "exclude");
        Assert.Equal(["wishlist", "ordinary"], withoutRecurring.Items.Select(item => item.Id));

        var wishlistOnly = await service.GetTransactionsAsync(all: true, wishlistFilter: "only");
        Assert.Equal("wishlist", Assert.Single(wishlistOnly.Items).Id);
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

    [Theory]
    [InlineData("amount-desc", "largest", "middle", "smallest")]
    [InlineData("amount-asc", "smallest", "middle", "largest")]
    public async Task GetTransactionsAsync_SortsPagedResultsByAbsoluteAmount(
        string sort,
        params string[] expectedIds)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("smallest", "Coffee", "Food", "Rewards", -10m),
            NewTransaction("middle", "Refund", "Other", "Rewards", 50m),
            NewTransaction("largest", "Rent", "Housing", "Essentials", -1000m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, pageSize: 10, sort: sort);

        Assert.Equal(expectedIds, result.Items.Select(item => item.Id));
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
    public async Task WriteTransactionsCsvAsync_PreservesSortAndNeutralizesSpreadsheetFormulas()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("small", "=HYPERLINK(\"bad\")", "+Food", "Rewards", -10m),
            NewTransaction("large", "Rent", "Housing", "Essentials", -1000m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);
        await using var destination = new MemoryStream();

        await service.WriteTransactionsCsvAsync(destination, sort: "amount-asc");

        destination.Position = 0;
        using var reader = new StreamReader(destination);
        var csv = await reader.ReadToEndAsync();
        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("'=HYPERLINK", rows[1]);
        Assert.Contains("'+Food", rows[1]);
        Assert.Contains("Rent", rows[2]);
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

    [Fact]
    public async Task GetAutocompleteSuggestionsAsync_ExcludesCommitmentCompletions()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("ordinary-1", "Prawn Noodle Soup", "Food", "Essentials", -10m),
            NewTransaction("completion-1", "Completed commitment: Car service", "Other", "Rewards", -1200m, savingsGoalId: 7));
        await context.SaveChangesAsync();

        var suggestions = await new TransactionQueryService(context).GetAutocompleteSuggestionsAsync();

        Assert.Equal("Prawn Noodle Soup", Assert.Single(suggestions).Description);
    }

    [Fact]
    public async Task GetAutocompleteSuggestionsAsync_UsesTheStoredMarkerInsteadOfDescriptionText()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var marked = NewTransaction("marked-row", "A normal-looking description", "Food", "Essentials", -8m);
        marked.ExcludeFromAutocomplete = true;
        context.Transactions.AddRange(
            NewTransaction("ordinary-alignment", "Account balance alignment - RYT", "Food", "Essentials", -20m),
            NewTransaction("ordinary-purchased", "Purchased: lunch", "Food", "Essentials", -12m),
            marked);
        await context.SaveChangesAsync();

        var suggestions = await new TransactionQueryService(context).GetAutocompleteSuggestionsAsync();

        Assert.Equal(
            ["Account balance alignment - RYT", "Purchased: lunch"],
            suggestions.Select(suggestion => suggestion.Description).OrderBy(description => description));
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
        int? wishlistItemId = null,
        int? savingsGoalId = null)
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
            WishlistItemId = wishlistItemId,
            SavingsGoalId = savingsGoalId,
            ExcludeFromAutocomplete = TransactionAutocompletePolicy.ShouldExclude(
                id, category, ledgerCategory, wishlistItemId, savingsGoalId)
        };
    }
}
