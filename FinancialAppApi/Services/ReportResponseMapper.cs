using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

public static class ReportResponseMapper
{
    public static object ObfuscateSummaryInsights(ReportSummaryInsights insights) => new
    {
        largestExpenseDescription = insights.LargestExpenseDescription,
        largestExpenseAmount = ObfuscateNullable(insights.LargestExpenseAmount),
        biggestDayDate = insights.BiggestDayDate,
        biggestDayTotal = ObfuscateNullable(insights.BiggestDayTotal),
        avgDailySpend = ObfuscateNullable(insights.AverageDailySpend),
        cycleLengthDays = insights.CycleLengthDays,
        velocityFirstHalf = ObfuscateNullable(insights.VelocityFirstHalf),
        velocitySecondHalf = ObfuscateNullable(insights.VelocitySecondHalf),
        noSpendDays = insights.NoSpendDays,
        transactionCount = insights.ExpenseEntryCount,
        committedSpend = ObfuscationHelper.Obfuscate(insights.CommittedSpend),
        discretionarySpend = ObfuscationHelper.Obfuscate(insights.DiscretionarySpend)
    };

    private static string? ObfuscateNullable(decimal? amount) =>
        amount.HasValue ? ObfuscationHelper.Obfuscate(amount.Value) : null;
}
