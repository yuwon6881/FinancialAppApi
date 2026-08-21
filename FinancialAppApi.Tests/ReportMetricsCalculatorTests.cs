using FinancialAppApi.Models;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public sealed class ReportMetricsCalculatorTests
{
    private static Transaction Expense(string date, decimal amount, string category = "Food", string? recurringPaymentId = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Date = DateTime.SpecifyKind(DateTime.Parse(date), DateTimeKind.Utc),
        Amount = -Math.Abs(amount),
        Category = category,
        LedgerCategory = "Essentials",
        Description = "Test expense",
        RecurringPaymentId = recurringPaymentId
    };

    [Fact]
    public void BuildSummaryInsights_InProgressCycle_AveragesOverElapsedDaysNotWholeCycle()
    {
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var endExclusive = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var asOf = new DateOnly(2026, 8, 10); // 10 elapsed days of a 31-day cycle

        var insights = ReportMetricsCalculator.BuildSummaryInsights(
            [Expense("2026-08-02", 100), Expense("2026-08-05", 100)],
            start,
            endExclusive,
            asOf);

        Assert.Equal(20m, insights.AverageDailySpend);
    }

    [Fact]
    public void BuildSummaryInsights_CompletedCycle_AveragesOverTheFullCycle()
    {
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var endExclusive = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        var insights = ReportMetricsCalculator.BuildSummaryInsights(
            [Expense("2026-08-02", 155)],
            start,
            endExclusive,
            new DateOnly(2026, 9, 5));

        Assert.Equal(5m, insights.AverageDailySpend);
    }

    [Fact]
    public void BuildSummaryInsights_CycleNotStarted_LeavesAverageUnknown()
    {
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var endExclusive = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        var insights = ReportMetricsCalculator.BuildSummaryInsights(
            [Expense("2026-08-02", 100)],
            start,
            endExclusive,
            new DateOnly(2026, 7, 20));

        Assert.Null(insights.AverageDailySpend);
    }

    [Fact]
    public void BuildSummaryInsights_UpcomingCycle_ReturnsZeroNoSpendDays()
    {
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var endExclusive = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var asOf = new DateOnly(2026, 7, 25);

        var insights = ReportMetricsCalculator.BuildSummaryInsights([], start, endExclusive, asOf);

        Assert.Equal(31, insights.CycleLengthDays);
        Assert.Equal(0, insights.NoSpendDays);
    }

    [Fact]
    public void BuildSummaryInsights_InProgressCycle_CountsOnlyElapsedDaysMinusElapsedExpenseDays()
    {
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var endExclusive = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var asOf = new DateOnly(2026, 8, 10); // 10 elapsed days (Aug 1 to Aug 10)

        var transactions = new List<Transaction>
        {
            Expense("2026-08-02", 15),
            Expense("2026-08-05", 30),
            // Future expense scheduled for Aug 20 should not be counted against elapsed days
            Expense("2026-08-20", 50)
        };

        var insights = ReportMetricsCalculator.BuildSummaryInsights(transactions, start, endExclusive, asOf);

        Assert.Equal(31, insights.CycleLengthDays);
        // 10 elapsed days - 2 distinct expense days in the elapsed window = 8 no-spend days
        Assert.Equal(8, insights.NoSpendDays);
    }

    [Fact]
    public void BuildSummaryInsights_PastCompletedCycle_CountsAllDaysMinusExpenseDays()
    {
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var endExclusive = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var asOf = new DateOnly(2026, 9, 5); // Past cycle

        var transactions = new List<Transaction>
        {
            Expense("2026-08-02", 15),
            Expense("2026-08-05", 30),
            Expense("2026-08-20", 50)
        };

        var insights = ReportMetricsCalculator.BuildSummaryInsights(transactions, start, endExclusive, asOf);

        Assert.Equal(31, insights.CycleLengthDays);
        // 31 total days - 3 distinct expense days = 28 no-spend days
        Assert.Equal(28, insights.NoSpendDays);
    }
}
