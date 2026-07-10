using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

// Phase 4: enforcement of the sufficiency verdict. Once context is loaded and evaluated, this
// decides -- deterministically, before any model call -- whether a required dataset is missing
// in a way that makes an accurate answer impossible. Only datasets that an intent needs EXACTLY
// (a count, a total, a forecast) can block: an intent that merely wants general guidance
// (e.g. allocation advice with budget targets hidden by sensitive mode) is allowed through so
// the model can still give qualitative help without inventing numbers.
public partial class AiAssistantService
{
    // The dataset keys that at least one requested intent requires an EXACT figure for. A
    // missing (hidden/unavailable) dataset in this set cannot be answered precisely and is not
    // recoverable here, so it blocks generation.
    private static HashSet<AiDatasetKey> ExactRequiredDatasets(IReadOnlyList<string> intents)
    {
        var set = new HashSet<AiDatasetKey>();
        foreach (var intent in intents)
        {
            if (IntentDataRequirements.TryGetValue(intent, out var requirement) && requirement.RequiresExact)
            {
                foreach (var dataset in requirement.Datasets) set.Add(dataset);
            }
        }
        return set;
    }

    // Returns a deterministic block response when an exact-required dataset is missing, otherwise
    // null (generation proceeds). Replaces the previous wishlist-forecast-only special case with a
    // rule that applies uniformly to every intent that needs an exact figure.
    private static AiChatResponse? ResolveInsufficiency(
        IReadOnlyList<string> intents,
        ContextSufficiency sufficiency)
    {
        if (sufficiency.Complete) return null;

        var exactRequired = ExactRequiredDatasets(intents);
        // Any hidden/unavailable requirement is a hard gate. Exact metrics receive a precise
        // refusal; qualitative intents also must not be handed a prompt that silently omits a
        // required protected dataset.
        var blocking = sufficiency.Missing
            .Where(missing => exactRequired.Contains(missing)
                || missing == AiDatasetKey.Recurring
                || missing == AiDatasetKey.Wishlist)
            .ToList();
        if (blocking.Count == 0) return null;

        if (blocking.Contains(AiDatasetKey.WishlistForecast))
        {
            return new AiChatResponse(
                "I can't calculate an exact wishlist target while sensitive mode is enabled. Unhide amounts in settings to use this forecast.",
                []);
        }
        return new AiChatResponse(
            "I don't have enough data to answer that precisely right now. Please try a narrower cycle or check the relevant view directly.",
            []);
    }

    // The one deterministic recovery this pipeline currently supports: replace a truncated
    // sample's outflow sum with an exact database SUM over the full match set. Only called once
    // the sufficiency validator has flagged "cycleSummaries" as recoverable, so this never runs
    // speculatively. Recovered PER CYCLE (not one merged figure across every requested cycle) so
    // a multi-cycle comparison question gets an exact outflow for each cycle it names, not a
    // single blended total that would misrepresent a per-cycle breakdown.
    private async Task<IReadOnlyDictionary<CycleKey, decimal>> RecoverCycleOutflowByRangeAsync(
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        string? searchFilter,
        IReadOnlyList<string>? transactionIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<CycleKey, decimal>();
        foreach (var cycle in cycles)
        {
            var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
            var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
            var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
            result[cycle] = await SumOutflowAsync(start, end, searchFilter, transactionIds, cancellationToken);
        }
        return result;
    }
}
