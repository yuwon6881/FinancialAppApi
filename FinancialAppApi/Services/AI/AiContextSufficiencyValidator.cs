namespace FinancialAppApi.Services;

// Phase 5: an explicit, testable gate between "context loaded" and "generate an answer".
// Every dataset a question depends on has a status; every intent declares which datasets it
// needs and whether an exact figure is required. From that the validator decides whether the
// question can be answered, whether the answer must be hedged as approximate, and what (if
// anything) is missing but deterministically recoverable (e.g. re-run a COUNT/SUM).
// Phase 4/10: the typed set of datasets the sufficiency pipeline reasons about. Replacing bare
// strings means a typo in a dataset key is a compile error, not a silent "always missing".
public enum AiDatasetKey
{
    CycleSummaries,
    TransactionDetails,
    TransactionMatches,
    Wishlist,
    WishlistForecast,
    Recurring,
    BudgetTargets
}

public partial class AiAssistantService
{
    internal enum AiDatasetStatus
    {
        Available,      // present and complete
        VerifiedEmpty,  // queried, genuinely no rows -> a valid "none" answer, NOT missing
        Hidden,         // withheld by sensitive mode -> blocked, not recoverable here
        Truncated,      // capped by MaxTransactionsPerRange -> aggregates are approximate
        Unavailable     // required but not loaded
    }

    internal sealed record AiDatasetState(
        AiDatasetStatus Status,
        int? TotalCount = null,
        int IncludedCount = 0,
        bool HasExactMetric = false,
        string? Reason = null);

    internal sealed record DataRequirement(AiDatasetKey DatasetKey, string Reason);

    internal sealed record SufficiencyResult(
        bool CanAnswer,
        bool IsApproximate,
        IReadOnlyList<DataRequirement> Missing,
        IReadOnlyList<DataRequirement> Recoverable);

    // Which dataset keys each intent needs, and whether it requires an EXACT figure (so a
    // truncated dataset without a recovered exact metric cannot be answered precisely).
    private static readonly Dictionary<string, (AiDatasetKey[] Datasets, bool RequiresExact)> IntentDataRequirements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ledger.activity_count"] = ([AiDatasetKey.TransactionMatches], true),
        ["ledger.merchant_search"] = ([AiDatasetKey.TransactionMatches], false),
        ["ledger.spending_total"] = ([AiDatasetKey.CycleSummaries], true),
        ["ledger.comparison"] = ([AiDatasetKey.CycleSummaries], true),
        ["ledger.transaction_list"] = ([AiDatasetKey.TransactionDetails], false),
        ["ledger.anomaly"] = ([AiDatasetKey.TransactionDetails], false),
        ["ledger.duplicates"] = ([AiDatasetKey.TransactionDetails], false),
        ["wishlist.list"] = ([AiDatasetKey.Wishlist], false),
        ["wishlist.forecast"] = ([AiDatasetKey.WishlistForecast], true),
        ["recurring.list"] = ([AiDatasetKey.Recurring], false),
        ["recurring.upcoming"] = ([AiDatasetKey.Recurring], false),
        ["allocation.balance"] = ([AiDatasetKey.CycleSummaries, AiDatasetKey.BudgetTargets], false),
        ["allocation.performance"] = ([AiDatasetKey.CycleSummaries, AiDatasetKey.BudgetTargets], false)
    };

    internal static SufficiencyResult EvaluateSufficiency(
        IReadOnlyList<string> intents,
        IReadOnlyDictionary<AiDatasetKey, AiDatasetState> datasets)
    {
        var missing = new List<DataRequirement>();
        var recoverable = new List<DataRequirement>();
        var approximate = false;

        foreach (var intent in intents)
        {
            if (!IntentDataRequirements.TryGetValue(intent, out var requirement)) continue;
            foreach (var key in requirement.Datasets)
            {
                if (!datasets.TryGetValue(key, out var state))
                {
                    missing.Add(new DataRequirement(key, "required dataset was not loaded"));
                    continue;
                }

                switch (state.Status)
                {
                    case AiDatasetStatus.Available:
                    case AiDatasetStatus.VerifiedEmpty:
                        break;
                    case AiDatasetStatus.Hidden:
                        missing.Add(new DataRequirement(key, state.Reason ?? "hidden by sensitive mode"));
                        break;
                    case AiDatasetStatus.Unavailable:
                        missing.Add(new DataRequirement(key, state.Reason ?? "unavailable"));
                        break;
                    case AiDatasetStatus.Truncated:
                        if (state.HasExactMetric)
                        {
                            // Recovered deterministically (exact COUNT/SUM); still note the sample is partial.
                            recoverable.Add(new DataRequirement(key, "sample truncated but exact metric recovered"));
                        }
                        else if (requirement.RequiresExact)
                        {
                            approximate = true;
                            recoverable.Add(new DataRequirement(key, "truncated; recover via exact aggregate query"));
                        }
                        else
                        {
                            approximate = true;
                        }
                        break;
                }
            }
        }

        return new SufficiencyResult(
            CanAnswer: missing.Count == 0,
            IsApproximate: approximate,
            Missing: missing,
            Recoverable: recoverable);
    }

    // Phase 5.5: never let an incomplete aggregate be reported as an exact number. If the
    // answer is approximate and the model's reply reads as a bare exact figure, prefix a hedge.
    private static string EnforceApproximateWording(string reply, bool isApproximate)
    {
        if (!isApproximate || string.IsNullOrWhiteSpace(reply)) return reply;
        var lower = reply.ToLowerInvariant();
        var alreadyHedged = lower.Contains("approx") || lower.Contains("about ") || lower.Contains("around ")
            || lower.Contains("roughly") || lower.Contains("at least") || lower.Contains("~")
            || lower.Contains("partial") || lower.Contains("more than");
        return alreadyHedged ? reply : $"Approximately: {reply}";
    }
}
