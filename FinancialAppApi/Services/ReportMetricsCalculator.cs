using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services;

public sealed record ReportBreakdownItem(string Category, decimal Amount);

public sealed record ReportSummaryInsights(
    string? LargestExpenseDescription,
    decimal? LargestExpenseAmount,
    string? BiggestDayDate,
    decimal? BiggestDayTotal,
    decimal? AverageDailySpend,
    int CycleLengthDays,
    decimal? VelocityFirstHalf,
    decimal? VelocitySecondHalf,
    int NoSpendDays,
    int ExpenseEntryCount,
    decimal CommittedSpend,
    decimal DiscretionarySpend);

public static class ReportMetricsCalculator
{
    public static List<ReportBreakdownItem> BuildBreakdown(IEnumerable<Transaction> transactions) =>
        transactions
            .Where(TransactionReportSemantics.IsReportableOutflow)
            .GroupBy(
                transaction => string.IsNullOrWhiteSpace(transaction.Category) ? "Other" : transaction.Category.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new ReportBreakdownItem(group.Key, Math.Abs(group.Sum(item => item.Amount))))
            .OrderByDescending(item => item.Amount)
            .ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static ReportSummaryInsights BuildSummaryInsights(
        IEnumerable<Transaction> transactions,
        DateTime start,
        DateTime endExclusive)
    {
        var expenses = transactions.Where(TransactionReportSemantics.IsReportableOutflow).ToList();
        var largest = expenses.OrderBy(transaction => transaction.Amount).FirstOrDefault();
        var biggestDay = expenses
            .GroupBy(transaction => TransactionDate.ToDateOnly(transaction.Date))
            .Select(group => new { Date = group.Key, Total = Math.Abs(group.Sum(item => item.Amount)) })
            .OrderByDescending(day => day.Total)
            .FirstOrDefault();

        var startDate = DateOnly.FromDateTime(start);
        var endDate = DateOnly.FromDateTime(endExclusive);
        var cycleLengthDays = Math.Max(0, endDate.DayNumber - startDate.DayNumber);
        var firstHalfDays = (cycleLengthDays + 1) / 2;
        var secondHalfStart = startDate.AddDays(firstHalfDays);
        var totalSpend = Math.Abs(expenses.Sum(transaction => transaction.Amount));
        var firstHalf = expenses
            .Where(transaction => TransactionDate.ToDateOnly(transaction.Date) < secondHalfStart)
            .Sum(transaction => Math.Abs(transaction.Amount));
        var secondHalf = totalSpend - firstHalf;
        var distinctExpenseDays = expenses
            .Select(transaction => TransactionDate.ToDateOnly(transaction.Date))
            .Distinct()
            .Count();

        return new ReportSummaryInsights(
            largest?.Description,
            largest is null ? null : Math.Abs(largest.Amount),
            biggestDay?.Date.ToString("yyyy-MM-dd"),
            biggestDay?.Total,
            expenses.Count > 0 && cycleLengthDays > 0 ? totalSpend / cycleLengthDays : null,
            cycleLengthDays,
            expenses.Count > 0 ? firstHalf : null,
            expenses.Count > 0 ? secondHalf : null,
            Math.Max(0, cycleLengthDays - distinctExpenseDays),
            expenses.Count,
            Math.Abs(expenses.Where(transaction => !string.IsNullOrEmpty(transaction.RecurringPaymentId)).Sum(transaction => transaction.Amount)),
            Math.Abs(expenses.Where(transaction => string.IsNullOrEmpty(transaction.RecurringPaymentId)).Sum(transaction => transaction.Amount)));
    }

    public static decimal IncomeAllocatedTo(IEnumerable<Transaction> transactions, string bucket) =>
        transactions
            .Where(transaction =>
                transaction.Amount > 0m &&
                (transaction.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase) ||
                 transaction.LedgerCategory.StartsWith("Transfer:Income->", StringComparison.OrdinalIgnoreCase)))
            .Sum(transaction => CategoryAttributionService.GetCategoryAmount(transaction, bucket));
}
