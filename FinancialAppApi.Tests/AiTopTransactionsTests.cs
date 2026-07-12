using System.Text.Json;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

// Superlative single-record ranking ("biggest/largest/smallest transaction", "most expensive
// purchase", "biggest income"). Verifies the exact server-side metric that replaces the model
// eyeballing a mixed sample -- transfers excluded, spending and income ranked separately.
public class AiTopTransactionsTests
{
    private static AiAssistantService.AiTransactionRow Row(
        string id, string description, decimal amount,
        string category = "Food", string ledger = "Essentials")
        => new(id, new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc),
            "2026-07-10", description, category, ledger, amount);

    private static JsonElement Build(IReadOnlyList<AiAssistantService.AiTransactionRow> rows, string query)
        => JsonSerializer.SerializeToElement(AiAssistantService.BuildTopTransactions(rows, query));

    [Fact]
    public void LargestOutflow_IsRankedFirst()
    {
        var result = Build(
        [
            Row("a", "Coffee", -8),
            Row("b", "Laptop", -300),
            Row("c", "Lunch", -12)
        ], "what's the biggest transaction recently");

        var outflows = result.GetProperty("outflows");
        Assert.Equal("outflow", result.GetProperty("direction").GetString());
        Assert.Equal("largest", result.GetProperty("order").GetString());
        Assert.Equal("b", outflows[0].GetProperty("Id").GetString());
        Assert.Equal(300m, outflows[0].GetProperty("amount").GetDecimal());
    }

    [Fact]
    public void Transfers_AreExcluded()
    {
        var result = Build(
        [
            Row("t", "Move to savings", -5000, category: "Transfer", ledger: "Transfer:Stability"),
            Row("a", "Rent", -900)
        ], "biggest spending");

        var outflows = result.GetProperty("outflows");
        Assert.Equal(1, outflows.GetArrayLength());
        Assert.Equal("a", outflows[0].GetProperty("Id").GetString());
    }

    [Fact]
    public void Inflows_AreRankedSeparatelyFromOutflows()
    {
        var result = Build(
        [
            Row("in", "Salary", 4000, category: "Salary", ledger: "Income"),
            Row("out", "Groceries", -150)
        ], "largest spending this cycle");

        // A big inflow must never appear as spending.
        var outflows = result.GetProperty("outflows");
        Assert.Equal(1, outflows.GetArrayLength());
        Assert.Equal("out", outflows[0].GetProperty("Id").GetString());

        var inflows = result.GetProperty("inflows");
        Assert.Equal("in", inflows[0].GetProperty("Id").GetString());
    }

    [Fact]
    public void CheapestQuestion_RanksSmallestFirst()
    {
        var result = Build(
        [
            Row("a", "Coffee", -8),
            Row("b", "Laptop", -300),
            Row("c", "Gum", -2)
        ], "what was my cheapest purchase");

        Assert.Equal("smallest", result.GetProperty("order").GetString());
        Assert.Equal("c", result.GetProperty("outflows")[0].GetProperty("Id").GetString());
    }

    [Fact]
    public void IncomeQuestion_HintsInflowDirection()
    {
        var result = Build(
        [
            Row("in", "Bonus", 1500, category: "Salary", ledger: "Income"),
            Row("out", "Dinner", -40)
        ], "what was my biggest deposit");

        Assert.Equal("inflow", result.GetProperty("direction").GetString());
        Assert.Equal("in", result.GetProperty("inflows")[0].GetProperty("Id").GetString());
    }

    [Fact]
    public void WantsTopTransactions_MatchesSuperlativesOnly()
    {
        Assert.True(AiAssistantService.WantsTopTransactions("biggest transaction"));
        Assert.True(AiAssistantService.WantsTopTransactions("most expensive purchase"));
        Assert.True(AiAssistantService.WantsTopTransactions("smallest charge"));
        Assert.False(AiAssistantService.WantsTopTransactions("how much did I spend"));
    }
}
