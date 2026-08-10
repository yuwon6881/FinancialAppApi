using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

// Phase 7: deterministic duplicate/anomaly detection. Both replace naive heuristics:
//   * duplicates were an exact (description, amount, date) group -- it missed next-day repeats
//     and same-merchant/different-day charges. Now: normalized merchant + amount tolerance +
//     a configurable day window, with human-readable reasons and a confidence score.
//   * anomalies were "the 5 largest transactions", which always flags the biggest spend even
//     when it is perfectly normal. Now: compared against the per-category median via the
//     modified z-score (median absolute deviation), with a minimum sample size so a category
//     with too little history is never judged.
public partial class AiAssistantService
{
    internal sealed record DuplicateCandidate(
        IReadOnlyList<string> Ids,
        string Description,
        decimal Amount,
        int DaysApart,
        double Confidence,
        IReadOnlyList<string> Reasons);

    internal sealed record AnomalyResult(
        string Id,
        string Description,
        string Category,
        decimal Amount,
        double Score,
        string Reason);

    internal const int DefaultDuplicateWindowDays = 3;
    internal const decimal DefaultAmountTolerance = 0.01m;
    internal const int MinAnomalySampleSize = 4;
    internal const double AnomalyScoreThreshold = 3.5;

    internal static IReadOnlyList<DuplicateCandidate> DetectDuplicates(
        IReadOnlyList<AiTransactionRow> transactions,
        int windowDays = DefaultDuplicateWindowDays,
        decimal amountTolerance = DefaultAmountTolerance)
    {
        var candidates = new List<DuplicateCandidate>();
        var groups = transactions
            .Where(t => TransactionReportSemantics.IsReportableCashMovement(t.Amount, t.Category, t.LedgerCategory))
            .GroupBy(t => (Merchant: NormalizeMerchant(t.Description), Sign: Math.Sign(t.Amount)));

        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Key.Merchant)) continue;
            var ordered = group.OrderBy(t => t.Timestamp).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                for (var j = i + 1; j < ordered.Count; j++)
                {
                    var a = ordered[i];
                    var b = ordered[j];
                    var daysApart = Math.Abs((b.Timestamp.Date - a.Timestamp.Date).Days);
                    if (daysApart > windowDays) break; // ordered by time; nothing closer further out
                    if (Math.Abs(Math.Abs(a.Amount) - Math.Abs(b.Amount)) > amountTolerance) continue;

                    var reasons = new List<string> { "same merchant", "same amount" };
                    reasons.Add(daysApart == 0 ? "posted same day" : $"posted {daysApart} day(s) apart");
                    if (a.Category.Equals(b.Category, StringComparison.OrdinalIgnoreCase)) reasons.Add("same category");

                    var confidence = 0.6
                        + (daysApart == 0 ? 0.3 : daysApart == 1 ? 0.2 : 0.1)
                        + (a.Category.Equals(b.Category, StringComparison.OrdinalIgnoreCase) ? 0.05 : 0);
                    candidates.Add(new DuplicateCandidate(
                        [a.Id, b.Id],
                        a.Description,
                        Math.Abs(a.Amount),
                        daysApart,
                        Math.Round(Math.Min(confidence, 0.99), 2),
                        reasons));
                }
            }
        }

        return candidates
            .OrderByDescending(c => c.Confidence)
            .Take(20)
            .ToList();
    }

    internal static IReadOnlyList<AnomalyResult> DetectAnomalies(
        IReadOnlyList<AiTransactionRow> transactions,
        int minSampleSize = MinAnomalySampleSize,
        double scoreThreshold = AnomalyScoreThreshold)
    {
        var anomalies = new List<AnomalyResult>();
        var outflowsByCategory = transactions
            .Where(t => t.Amount < 0 && TransactionReportSemantics.IsReportableCashMovement(t.Amount, t.Category, t.LedgerCategory))
            .GroupBy(t => t.Category);

        foreach (var group in outflowsByCategory)
        {
            var rows = group.ToList();
            if (rows.Count < minSampleSize) continue; // too little history to judge

            var magnitudes = rows.Select(t => (double)Math.Abs(t.Amount)).ToList();
            var median = MedianOf(magnitudes);
            var mad = MedianOf(magnitudes.Select(m => Math.Abs(m - median)).ToList());
            if (mad <= 0) continue; // no spread -> nothing is an outlier

            foreach (var row in rows)
            {
                var magnitude = (double)Math.Abs(row.Amount);
                // Only spikes above the median count as anomalies, not unusually small spends.
                if (magnitude <= median) continue;
                var score = 0.6745 * (magnitude - median) / mad;
                if (score < scoreThreshold) continue;
                var multiple = median > 0 ? magnitude / median : 0;
                anomalies.Add(new AnomalyResult(
                    row.Id, row.Description, row.Category, row.Amount, Math.Round(score, 2),
                    $"{multiple:0.#}x the median {row.Category} spend"));
            }
        }

        return anomalies
            .OrderByDescending(a => a.Score)
            .Take(5)
            .ToList();
    }

    private static readonly Regex MerchantNoise = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);

    private static string NormalizeMerchant(string description)
    {
        var cleaned = MerchantNoise.Replace(description ?? string.Empty, " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim().ToLowerInvariant();
    }

    private static double MedianOf(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(x => x).ToList();
        var count = sorted.Count;
        return count % 2 == 1
            ? sorted[count / 2]
            : (sorted[(count / 2) - 1] + sorted[count / 2]) / 2.0;
    }
}
