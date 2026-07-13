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

    private static Transaction NewTransaction(string id, string description, string category, string ledgerCategory, decimal amount)
    {
        return new Transaction
        {
            Id = id,
            Date = TransactionDate.FromInputDate(new DateOnly(2026, 7, 9)),
            Description = description,
            Category = category,
            LedgerCategory = ledgerCategory,
            Amount = amount
        };
    }
}
