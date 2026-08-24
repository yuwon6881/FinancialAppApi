using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinancialAppApi.Services.Accounts;

/// <summary>
/// Serializes cumulative per-account ending balances for the <c>CycleBalance</c> cache.
/// <para>
/// Account balances are a placement of the same bucket legs <c>CycleBalance</c> already totals, so
/// this cache introduces no new money and no new invalidation rule: it rides on the row that
/// <see cref="CycleBalanceService"/> already deletes whenever a transaction dated at or before that
/// cycle changes.
/// </para>
/// <para>
/// Only non-zero balances are stored, so an account missing from the payload reads as zero. That is
/// correct rather than lossy: an account with no ledger activity before the cached cycle has a zero
/// balance there, and an account created later cannot have had one at all. Creating an account with
/// an opening amount writes a transaction, which invalidates the affected rows through the normal
/// path.
/// </para>
/// </summary>
public static class LedgerAccountBalanceCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Always returns a payload, never null: a cycle whose accounts all ended at zero must still
    /// serialize to <c>{}</c> so that a null column can mean exactly one thing -- "written before
    /// this cache existed, so rescan" -- rather than being ambiguous with a genuinely empty ledger.
    /// </summary>
    public static string Serialize(IReadOnlyDictionary<string, decimal>? balances)
    {
        var open = (balances ?? new Dictionary<string, decimal>(StringComparer.Ordinal))
            .Where(balance => balance.Value != 0m)
            .OrderBy(balance => balance.Key, StringComparer.Ordinal)
            .ToDictionary(balance => balance.Key, balance => balance.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(open, Options);
    }

    /// <summary>
    /// Returns null for absent or unreadable cached state, which the caller must treat as "no
    /// snapshot available" and answer from the full-history scan instead. Rows persisted before the
    /// column existed carry null, so failing closed here is what keeps them correct.
    /// </summary>
    public static IReadOnlyDictionary<string, decimal>? Deserialize(string? cached)
    {
        if (string.IsNullOrWhiteSpace(cached)) return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, decimal>>(cached, Options);
            return parsed is null
                ? null
                : new Dictionary<string, decimal>(parsed, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
