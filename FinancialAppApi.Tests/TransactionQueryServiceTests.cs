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
    public async Task ExportTransactionsAsync_IncludesCsvHeaderAndRows()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.Add(NewTransaction("tx-1", "Coffee", "Food", "Rewards", -10m));
        await context.SaveChangesAsync();
        var service = new TransactionQueryService(context);

        var result = await service.ExportTransactionsAsync();

        var csv = Encoding.UTF8.GetString(result.Bytes);
        Assert.Contains("Date,Description,Category,Ledger Category,Debit (Outflow),Credit (Inflow)", csv);
        Assert.Contains("Coffee", csv);
    }

    [Fact]
    public async Task GetAutocompleteSuggestionsAsync_ExcludesGeneratedIncomeSplits()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.AddRange(
            NewTransaction("income-1", "Salary", "Salary", "Income", 1000m),
            NewTransaction("income-1-split-0", "Salary - Essentials Split", "Salary", "Essentials", 400m),
            NewTransaction("income-1-split-1", "Salary - Growth Split", "Salary", "Growth", 300m));
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
