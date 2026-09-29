using System.Globalization;
using FinancialAppApi.Database;

namespace FinancialAppApi.Services.AI.Tools;

// Adapters onto the row and cycle types the existing pure calculators (anomaly and duplicate
// detection, forecasts) already accept, so tools reuse that arithmetic instead of copying it.
internal static class AiLegacyRows
{
    public static List<AiAssistantService.AiTransactionRow> From(IEnumerable<AiTransactionRecord> rows) =>
        rows.Select(row => new AiAssistantService.AiTransactionRow(
                row.Id,
                TransactionDate.StartOfDate(row.Date),
                row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.Description,
                row.Category,
                row.LedgerCategory,
                row.Amount,
                row.PostedAt,
                row.RecurringPaymentId,
                row.AccountId,
                row.CounterAccountId))
            .ToList();

    public static List<AiAssistantService.CycleKey> From(IEnumerable<AiCycle> cycles) =>
        cycles.Select(cycle => new AiAssistantService.CycleKey(cycle.Year, cycle.MonthIndex)).ToList();
}
