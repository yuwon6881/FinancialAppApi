using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinancialAppApi.Services.Stability;

/// <summary>
/// Serializes the reload queue's still-owing entries for the <c>CycleBalance</c> cache.
/// <para>
/// The cached aggregate (outstanding plus oldest date) cannot say which drawdown a carried
/// obligation came from, so a replay seeded from it alone falls back to one anonymous queue entry
/// and can no longer tell a partly-repaid drawdown from one already put back in full. Carrying the
/// identities is what lets every reported total count only what is still owed.
/// </para>
/// <para>
/// Only entries with money still owing are persisted: a discharged obligation has nothing left to
/// carry forward, and per-row completion status comes from
/// <see cref="StabilityReloadStatusService"/>'s full-history replay rather than from this cache.
/// </para>
/// </summary>
public static class StabilityReloadObligationCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record CachedObligation(
        string TransactionId,
        decimal OriginalAmount,
        decimal RemainingAmount,
        string? Date);

    /// <summary>Returns null when nothing is owed, so an empty queue stores an empty column.</summary>
    public static string? Serialize(IReadOnlyList<ReloadObligation>? obligations)
    {
        var open = (obligations ?? [])
            .Where(obligation => obligation.RemainingAmount > 0m)
            .Select(obligation => new CachedObligation(
                obligation.TransactionId,
                obligation.OriginalAmount,
                obligation.RemainingAmount,
                obligation.Date?.ToString("yyyy-MM-dd")))
            .ToList();
        return open.Count == 0 ? null : JsonSerializer.Serialize(open, Options);
    }

    /// <summary>
    /// Returns null for absent or unreadable cached state, which the caller must treat as "no
    /// identities available" and fall back to the aggregate. Failing closed to null rather than to
    /// an empty list matters: an empty list would claim nothing is owed.
    /// </summary>
    public static IReadOnlyList<ReloadObligation>? Deserialize(string? cached)
    {
        if (string.IsNullOrWhiteSpace(cached)) return null;
        List<CachedObligation>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<CachedObligation>>(cached, Options);
        }
        catch (JsonException)
        {
            return null;
        }
        if (parsed == null || parsed.Count == 0) return null;

        if (parsed.Any(obligation => string.IsNullOrEmpty(obligation.TransactionId)
                || obligation.OriginalAmount < obligation.RemainingAmount
                || obligation.RemainingAmount <= 0m)
            || parsed.Select(obligation => obligation.TransactionId)
                .Distinct(StringComparer.Ordinal).Count() != parsed.Count)
        {
            return null;
        }

        return parsed.Select(obligation => new ReloadObligation(
                obligation.TransactionId,
                obligation.OriginalAmount,
                obligation.RemainingAmount,
                DateOnly.TryParse(obligation.Date, out var date) ? date : null))
            .ToList();
    }

    /// <summary>
    /// Rebuilds the opening state for a cycle from the previous cycle's cached row. The aggregate
    /// stays authoritative for the amount owed; the identities only refine how it is attributed.
    /// </summary>
    public static ReloadState OpeningState(
        decimal outstanding,
        DateOnly? oldestDate,
        string? cachedObligations)
    {
        var obligations = Deserialize(cachedObligations);
        // The numeric column remains the authoritative cache contract. If an interrupted/manual
        // write left JSON that does not account for exactly that amount, discard the attribution
        // detail and replay the aggregate as one anonymous entry. Letting the detail win can create
        // or erase money at the cycle boundary.
        if (obligations is not null && obligations.Sum(item => item.RemainingAmount) != outstanding)
        {
            obligations = null;
        }
        return new ReloadState(outstanding, oldestDate, 0m, 0m, obligations);
    }
}
