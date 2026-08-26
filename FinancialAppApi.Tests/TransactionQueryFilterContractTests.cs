using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

/// <summary>
/// Filter, paging and branch-selection contracts for the ledger listing endpoint. These pin the
/// behaviours that had no coverage: the no-settings branch, page-offset arithmetic, the bucket
/// predicate, and the structural-row classification.
/// </summary>
public class TransactionQueryFilterContractTests
{
    [Fact]
    public async Task NoSettingsRow_ExcludesDiscardedAndAppliesFilters()
    {
        // Without a FinancialSettings row there is no cycle to bound the query by. That branch
        // used to skip ApplyAllFilters entirely, so soft-deleted rows surfaced in the ledger and
        // every filter the caller sent was ignored.
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("tx-keep", "Coffee", "Food", "Essentials", -10m),
            NewTransaction("tx-dropped", "Removed", "Food", "Discarded", -20m),
            NewTransaction("tx-filtered-out", "Salary", "Salary", "Income", 1000m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: false, txType: "outflow");

        Assert.Equal(["tx-keep"], result.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task NoSettingsRow_ExcludesLowercaseDiscardedMarker()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("tx-keep", "Coffee", "Food", "Essentials", -10m),
            NewTransaction("tx-legacy-marker", "Removed", "Food", "discarded", -20m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: false);

        Assert.Equal(["tx-keep"], result.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task LowercaseDiscardedMarkerStaysHiddenInAllMode()
    {
        // The writer side treats the marker case-insensitively; the reader now agrees.
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("tx-keep", "Coffee", "Food", "Essentials", -10m),
            NewTransaction("tx-legacy-marker", "Removed", "Food", "discarded", -20m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, page: 1, pageSize: 10);

        Assert.Equal(1, result.Total);
        Assert.Equal(["tx-keep"], result.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task PageBeyondIntRange_ReturnsEmptyInsteadOfThrowing()
    {
        // (page - 1) * pageSize overflowed int into a negative OFFSET, so a client could turn a
        // URL into an unhandled 500.
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.Add(NewTransaction("tx-1", "Coffee", "Food", "Essentials", -10m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, page: int.MaxValue, pageSize: 500);

        Assert.Empty(result.Items);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task PageBeyondLastPage_ReturnsEmptyPageAndEchoesRequestedPage()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.Add(NewTransaction("tx-1", "Coffee", "Food", "Essentials", -10m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, page: 9, pageSize: 10);

        // The client clamps against an empty page, so this must not silently serve the last page.
        Assert.Empty(result.Items);
        Assert.Equal(9, result.Page);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task CycleBranch_IgnoresFilterParameters()
    {
        // Pins the documented contract: filter, sort and paging apply to all:true only, because
        // bootstrap and export callers depend on getting the whole cycle back.
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            HideSensitive = false,
            Currency = "MYR"
        });
        context.Transactions.AddRange(
            NewTransaction("tx-out", "Coffee", "Food", "Essentials", -10m, date: new DateOnly(2026, 7, 5)),
            NewTransaction("tx-in", "Salary", "Salary", "Income", 1000m, date: new DateOnly(2026, 7, 6)));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: false, txType: "outflow", pageSize: 1);

        Assert.Equal(["tx-in", "tx-out"], result.Items.Select(item => item.Id));
        Assert.Null(result.Total);
    }

    [Theory]
    [InlineData("Stability")]
    [InlineData("stability")]
    public async Task BucketFilterClaimsBothLegsOfATransferRoute(string bucket)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("tx-route", "Top up", "Transfer", "Transfer:Stability->Rewards", 50m),
            NewTransaction("tx-plain", "Stability draw", "Other", "Stability", -50m),
            NewTransaction("tx-unrelated", "Coffee", "Food", "Essentials", -10m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, ledgerCategory: bucket);

        Assert.Equal(["tx-plain", "tx-route"], result.Items.Select(item => item.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task BucketFilterAcceptsSeveralBuckets()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("tx-essentials", "Coffee", "Food", "Essentials", -10m),
            NewTransaction("tx-growth", "Invest", "Other", "Growth", -20m),
            NewTransaction("tx-rewards", "Treat", "Other", "Rewards", -30m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, ledgerCategory: "Essentials,Rewards");

        Assert.Equal(
            ["tx-essentials", "tx-rewards"],
            result.Items.Select(item => item.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task IncomeBucketDoesNotClaimIncomeTransferRoutes()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("tx-income", "Salary", "Salary", "Income", 1000m),
            NewTransaction("tx-split-parent", "Salary", "Salary", "IncomeSplit:50,25,15,10", 1000m),
            NewTransaction("tx-split-leg", "[Split: Growth] Salary", "Transfer", "Transfer:Income->Growth", 250m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, ledgerCategory: "Income");

        Assert.Equal(
            ["tx-income", "tx-split-parent"],
            result.Items.Select(item => item.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task TransferTypeIncludesAccountMovesAndInflowExcludesThem()
    {
        // An account move is a structural row: it must appear under Transfer and must never be
        // counted as income, whichever of its two tokens a caller keys on.
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("tx-move", "Wallet to card", "Transfer", "AccountMove", 200m),
            NewTransaction("tx-salary", "Salary", "Salary", "Income", 1000m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var transfers = await service.GetTransactionsAsync(all: true, txType: "transfer");
        var inflows = await service.GetTransactionsAsync(all: true, txType: "inflow");

        Assert.Equal(["tx-move"], transfers.Items.Select(item => item.Id));
        Assert.Equal(["tx-salary"], inflows.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task UnknownTypeTokenCannotDisableTheFilter()
    {
        // Three tokens used to mean "every type selected", so inflow,foo,bar disabled the filter
        // and returned transfers and adjustments alongside the inflows.
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("tx-in", "Salary", "Salary", "Income", 1000m),
            NewTransaction("tx-out", "Coffee", "Food", "Essentials", -10m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, txType: "inflow,foo,bar");

        Assert.Equal(["tx-in"], result.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task SubCategoryFilterIgnoresCase()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("tx-food", "Coffee", "Food", "Essentials", -10m),
            NewTransaction("tx-transport", "Bus", "Transport", "Essentials", -2m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.GetTransactionsAsync(all: true, category: "food");

        Assert.Equal(["tx-food"], result.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task ClampsPageSizeToTheSupportedRange()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(Enumerable.Range(1, 3)
            .Select(index => NewTransaction($"tx-{index}", "Coffee", "Food", "Essentials", -index)));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var tooSmall = await service.GetTransactionsAsync(all: true, pageSize: 0);
        var tooLarge = await service.GetTransactionsAsync(all: true, pageSize: 10_000);

        Assert.Equal(1, tooSmall.PageSize);
        Assert.Single(tooSmall.Items);
        Assert.Equal(500, tooLarge.PageSize);
        Assert.Equal(3, tooLarge.Items.Count);
    }

    private static Transaction NewTransaction(
        string id,
        string description,
        string category,
        string ledgerCategory,
        decimal amount,
        DateOnly? date = null)
    {
        return new Transaction
        {
            Id = id,
            Date = TransactionDate.FromInputDate(date ?? new DateOnly(2026, 7, 9)),
            PostedAt = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc),
            Description = description,
            Category = category,
            LedgerCategory = ledgerCategory,
            Amount = amount,
        };
    }
}
