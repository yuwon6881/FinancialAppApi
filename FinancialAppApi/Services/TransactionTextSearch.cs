using System.Text.RegularExpressions;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

internal static partial class TransactionTextSearch
{
    internal const double FuzzyThreshold = 0.70d;

    private static readonly Regex WordPattern = new(
        @"[\p{L}\p{N}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static IQueryable<Transaction> ApplyExact(
        IQueryable<Transaction> query,
        bool useIlike,
        string searchText)
    {
        var term = searchText.Trim();
        if (term.Length == 0) return query;

        if (useIlike)
        {
            var pattern = $"%{EscapeLikePattern(term)}%";
            return query.Where(transaction =>
                EF.Functions.ILike(transaction.Description, pattern) ||
                EF.Functions.ILike(transaction.Category, pattern) ||
                EF.Functions.ILike(transaction.LedgerCategory, pattern));
        }

        var normalized = term.ToLowerInvariant();
        return query.Where(transaction =>
            transaction.Description.ToLower().Contains(normalized) ||
            transaction.Category.ToLower().Contains(normalized) ||
            transaction.LedgerCategory.ToLower().Contains(normalized));
    }

    internal static IQueryable<Transaction> ApplyFuzzy(
        IQueryable<Transaction> query,
        string searchText)
    {
        var term = searchText.Trim();
        return query.Where(transaction =>
            (EF.Functions.TrigramsAreStrictWordSimilar(term, transaction.Description) &&
             EF.Functions.TrigramsStrictWordSimilarity(term, transaction.Description) >= FuzzyThreshold) ||
            (EF.Functions.TrigramsAreStrictWordSimilar(term, transaction.Category) &&
             EF.Functions.TrigramsStrictWordSimilarity(term, transaction.Category) >= FuzzyThreshold) ||
            (EF.Functions.TrigramsAreStrictWordSimilar(term, transaction.LedgerCategory) &&
             EF.Functions.TrigramsStrictWordSimilarity(term, transaction.LedgerCategory) >= FuzzyThreshold));
    }

    // The production path uses PostgreSQL pg_trgm. This provider-independent equivalent keeps
    // in-memory tests honest without attempting to execute Npgsql-only functions client-side.
    internal static bool IsFuzzyMatch(string searchText, params string[] fields)
    {
        var searchWords = Words(searchText);
        if (searchWords.Count == 0) return false;
        var fieldWords = fields.SelectMany(Words).ToList();
        return searchWords.All(searchWord => fieldWords.Any(fieldWord =>
            Similarity(searchWord, fieldWord) >= FuzzyThreshold));
    }

    private static List<string> Words(string value) => WordPattern.Matches(value ?? string.Empty)
        .Select(match => match.Value.ToLowerInvariant())
        .ToList();

    private static double Similarity(string left, string right)
    {
        if (left == right) return 1d;
        if (left.Length == 0 || right.Length == 0) return 0d;

        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }
            (previous, current) = (current, previous);
        }

        return 1d - previous[right.Length] / (double)Math.Max(left.Length, right.Length);
    }

    private static string EscapeLikePattern(string input) => input
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
