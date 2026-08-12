using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Stability;

/// <summary>
/// Keeps an unchanged historical salary's generated rows stable when the parent is edited.
/// </summary>
public static class HistoricalIncomeSplitRows
{
    public static bool CanPreserve(Transaction parent, IReadOnlyCollection<Transaction> rows)
    {
        if (parent.Amount <= 0m || rows.Count == 0) return false;

        var prefix = parent.Id + "-split-";
        if (rows.Any(row => !row.Id.StartsWith(prefix, StringComparison.Ordinal)
            || !row.LedgerCategory.StartsWith("Transfer:Income->", StringComparison.OrdinalIgnoreCase)
            || row.Amount < 0m))
        {
            return false;
        }

        return Math.Abs(rows.Sum(row => row.Amount) - parent.Amount) <= 0.005m;
    }

    public static void RefreshMetadata(Transaction parent, IEnumerable<Transaction> rows)
    {
        const string transferPrefix = "Transfer:Income->";
        foreach (var row in rows)
        {
            row.Date = parent.Date;
            row.PostedAt = parent.PostedAt;
            row.Description = $"[Split: {row.LedgerCategory[transferPrefix.Length..]}] {parent.Description}";
        }
    }
}
