using FinancialAppApi.Models;

namespace FinancialAppApi.Services.AI;

public sealed record AiNamedAmount(string Name, decimal Amount, int Count);

public sealed record AiLedgerNet(string LedgerCategory, decimal Net);

public sealed record AiDayAmount(string Date, decimal Total);

public sealed record AiLargestExpense(string Id, string Date, string Description, decimal Amount);

public sealed record AiCycleInsights(
    int CycleLengthDays,
    int ElapsedDays,
    int RemainingDays,
    int NoSpendDaysObserved,
    decimal? AverageDailySpend,
    AiDayAmount? BiggestSpendingDay,
    AiLargestExpense? LargestExpense,
    decimal CommittedSpend,
    decimal DiscretionarySpend,
    decimal SpendFirstHalf,
    decimal SpendSecondHalf);

public sealed record AiCycleSummary(
    string Cycle,
    string Label,
    // Completed | InProgress | NotStarted. An in-progress cycle's figures are "so far".
    string Phase,
    string? ObservedThrough,
    bool HasTransactions,
    int TransactionCount,
    decimal Income,
    decimal Inflow,
    decimal OtherInflow,
    decimal Outflow,
    decimal NetChange,
    IReadOnlyList<AiNamedAmount> CategorySpend,
    IReadOnlyList<AiNamedAmount> OtherInflowByCategory,
    IReadOnlyList<AiLedgerNet> LedgerNet,
    AiCycleInsights? Insights);

// Pure cycle arithmetic over one cycle's rows, shared by the summary and comparison tools. It
// follows TransactionReportSemantics for cash flow and CategoryAttributionService for per-bucket
// net, the same rules the Reports and Dashboard screens use, so the assistant never disagrees
// with them.
public static class AiCycleSummaryCalculator
{
    // Money-bearing insight names the global masker does not already know.
    public static readonly IReadOnlySet<string> AmountProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "committedSpend", "discretionarySpend", "spendFirstHalf", "spendSecondHalf",
        "outflowChange", "incomeChange", "netChangeChange", "share"
    };

    private static readonly string[] Buckets = ["Essentials", "Growth", "Stability", "Rewards"];

    public static AiCycleSummary Build(
        AiCycle cycle,
        int cycleDay,
        DateOnly today,
        IReadOnlyList<AiTransactionRecord> rows,
        bool includeInsights,
        int topCategories = 8)
    {
        var range = AiCycleResolver.Range(cycle, cycleDay);
        var inCycle = rows.Where(row => row.Date >= range.FirstDate && row.Date <= range.LastDate).ToList();
        var reportable = inCycle
            .Where(row => TransactionReportSemantics.IsReportableCashMovement(row.Amount, row.Category, row.LedgerCategory))
            .ToList();
        var expenses = reportable.Where(row => row.Amount < 0).ToList();
        var inflows = reportable.Where(row => row.Amount > 0).ToList();
        var incomeRows = inflows
            .Where(row => TransactionReportSemantics.IsReportableIncome(row.Amount, row.Category, row.LedgerCategory))
            .ToList();

        var income = incomeRows.Sum(row => row.Amount);
        var inflow = inflows.Sum(row => row.Amount);
        var outflow = Math.Abs(expenses.Sum(row => row.Amount));
        var phase = today > range.LastDate ? "Completed" : today < range.FirstDate ? "NotStarted" : "InProgress";

        return new AiCycleSummary(
            cycle.Key,
            AiCycleResolver.Label(cycle, cycleDay),
            phase,
            phase == "NotStarted" ? null : (today < range.LastDate ? today : range.LastDate).ToString("yyyy-MM-dd"),
            inCycle.Count > 0,
            inCycle.Count,
            income,
            inflow,
            inflow - income,
            outflow,
            inflow - outflow,
            Group(expenses, topCategories),
            Group(inflows.Except(incomeRows).ToList(), topCategories),
            // Bucket net is summed over every row, transfers included: a Transfer:Source->Target
            // row is exactly what moves money between buckets.
            Buckets
                .Select(bucket => new AiLedgerNet(bucket, inCycle.Sum(row => CategoryAttributionService.GetCategoryAmount(
                    new Transaction { Amount = row.Amount, LedgerCategory = row.LedgerCategory }, bucket))))
                .ToList(),
            includeInsights ? Insights(range, today, expenses) : null);
    }

    private static List<AiNamedAmount> Group(IReadOnlyList<AiTransactionRecord> rows, int top) =>
        rows.GroupBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AiNamedAmount(group.First().Category, Math.Abs(group.Sum(row => row.Amount)), group.Count()))
            .OrderByDescending(entry => entry.Amount)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToList();

    private static AiCycleInsights Insights(AiDateRange range, DateOnly today, IReadOnlyList<AiTransactionRecord> expenses)
    {
        var totalDays = range.LastDate.DayNumber - range.FirstDate.DayNumber + 1;
        var elapsed = today < range.FirstDate ? 0 : today > range.LastDate ? totalDays : today.DayNumber - range.FirstDate.DayNumber + 1;
        var observedThrough = today < range.LastDate ? today : range.LastDate;
        var spendDays = expenses
            .Where(row => row.Date >= range.FirstDate && row.Date <= observedThrough)
            .Select(row => row.Date)
            .Distinct()
            .Count();
        var biggestDay = expenses
            .GroupBy(row => row.Date)
            .Select(group => new AiDayAmount(group.Key.ToString("yyyy-MM-dd"), Math.Abs(group.Sum(row => row.Amount))))
            .OrderByDescending(day => day.Total)
            .FirstOrDefault();
        var largest = expenses.OrderBy(row => row.Amount).ThenByDescending(row => row.Date).FirstOrDefault();
        var total = Math.Abs(expenses.Sum(row => row.Amount));
        var committed = Math.Abs(expenses.Where(row => row.RecurringPaymentId != null).Sum(row => row.Amount));
        var midpoint = range.FirstDate.AddDays(totalDays / 2);

        return new AiCycleInsights(
            totalDays,
            elapsed,
            Math.Max(0, totalDays - elapsed),
            Math.Max(0, elapsed - spendDays),
            elapsed > 0 ? Math.Round(total / elapsed, 2) : null,
            biggestDay,
            largest == null ? null : new AiLargestExpense(largest.Id, largest.Date.ToString("yyyy-MM-dd"), largest.Description, Math.Abs(largest.Amount)),
            committed,
            Math.Max(0, total - committed),
            Math.Abs(expenses.Where(row => row.Date < midpoint).Sum(row => row.Amount)),
            Math.Abs(expenses.Where(row => row.Date >= midpoint).Sum(row => row.Amount)));
    }
}
