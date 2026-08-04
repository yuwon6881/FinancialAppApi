using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private static object? BuildReportReviewContext(
        AiQueryPlan queryPlan,
        IReadOnlyList<CycleKey> cycles,
        IReadOnlyList<AiTransactionRow> transactions,
        int cycleDay,
        bool sensitiveMode)
    {
        if (!queryPlan.NeedsReport) return null;
        if (sensitiveMode)
        {
            return new
            {
                available = false,
                reason = "Report amounts and earlier history are hidden while sensitive mode is active.",
                historyRedacted = true
            };
        }

        var candidates = new List<object>();
        var anomalies = DetectAnomalies(transactions)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(2)
            .Select(item => (object)new
            {
                kind = "unusual spending",
                id = item.Id,
                description = item.Description,
                amount = Math.Abs(item.Amount),
                confidence = Math.Min(1d, item.Score / 10d),
                evidence = item.Reason
            });
        candidates.AddRange(anomalies);

        var duplicates = DetectDuplicates(transactions)
            .OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.Description, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Select(item => (object)new
            {
                kind = "possible duplicate",
                ids = item.Ids,
                description = item.Description,
                amount = Math.Abs(item.Amount),
                confidence = item.Confidence,
                evidence = string.Join("; ", item.Reasons)
            });
        candidates.AddRange(duplicates);

        var cycleEvidence = cycles
            .Select(cycle =>
            {
                var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
                var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
                var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
                var inCycle = transactions.Where(item => item.Timestamp >= start && item.Timestamp < end && !IsTransfer(item)).ToList();
                return new
                {
                    key = ToCycleKey(cycle),
                    outflow = Math.Abs(inCycle.Where(item => item.Amount < 0).Sum(item => item.Amount)),
                    transactionCount = inCycle.Count
                };
            })
            .ToList();

        return new
        {
            available = true,
            coverage = new
            {
                cycles = cycles.Select(ToCycleKey).ToList(),
                findingsAreCappedAt = 3,
                source = "server-computed cycle summaries and transaction checks"
            },
            cycleEvidence,
            findings = candidates.Take(3).ToList(),
            limitations = new[]
            {
                "The server selected evidence and calculated every amount and confidence.",
                "Explain no more than three findings and do not invent causes or missing records."
            }
        };
    }
}