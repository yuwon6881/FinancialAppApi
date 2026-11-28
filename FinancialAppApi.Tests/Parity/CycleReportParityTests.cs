using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests.Parity;

public sealed class CycleReportParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Parity", "Fixtures", "cycle-report.cases.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.Clone()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CycleReportMatchesCanonicalFixture(JsonElement item)
    {
        var input = item.GetProperty("input");
        var expenses = input.GetProperty("expenses").EnumerateArray().Select(expense => new Transaction
        {
            Date = TransactionDate.StartOfDate(DateOnly.Parse(expense.GetProperty("date").GetString()!)),
            Amount = -expense.GetProperty("amount").GetDecimal(),
            Category = "Food",
            LedgerCategory = "Essentials",
            Description = "Expense"
        });

        var result = ReportMetricsCalculator.BuildSummaryInsights(
            expenses,
            TransactionDate.StartOfDate(DateOnly.Parse(input.GetProperty("start").GetString()!)),
            TransactionDate.ExclusiveEndOfDate(DateOnly.Parse(input.GetProperty("end").GetString()!)),
            DateOnly.Parse(input.GetProperty("today").GetString()!));

        var expected = item.GetProperty("expected");
        Assert.Equal(expected.GetProperty("averageDailySpend").GetDecimal(), result.AverageDailySpend);
        Assert.Equal(expected.GetProperty("noSpendDays").GetInt32(), result.NoSpendDays);
        Assert.Equal(expected.GetProperty("expenseEntryCount").GetInt32(), result.ExpenseEntryCount);
    }
}
