using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests.Parity;

/// <summary>
/// Drives the canonical ledger filter/sort fixture through the real query builder.
///
/// Ordering is checked against <see cref="StringComparer.Ordinal"/> rather than through the
/// provider: the InMemory provider sorts strings with the culture-sensitive default comparer, so it
/// cannot demonstrate the ordinal tie-break the fixture pins. What the database is actually told to
/// do is asserted in TransactionQuerySqlTranslationTests, which compiles the real Npgsql SQL and
/// requires COLLATE "C" on every sort.
/// </summary>
public sealed class LedgerFilterSortParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [item.Clone()];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task LedgerFilteringMatchesCanonicalFixture(JsonElement item)
    {
        var input = item.GetProperty("input");
        var criteria = input.GetProperty("criteria");
        var expected = item.GetProperty("expected");

        await using var context = TestHelpers.NewInMemoryContext();
        foreach (var row in input.GetProperty("rows").EnumerateArray())
        {
            context.Transactions.Add(new Transaction
            {
                Id = row.GetProperty("id").GetString()!,
                UserId = TestHelpers.DefaultUserId,
                Date = TransactionDate.FromInputDate(
                    DateOnly.ParseExact(row.GetProperty("date").GetString()!, "yyyy-MM-dd")),
                PostedAt = row.GetProperty("postedAt").GetDateTimeOffset().UtcDateTime,
                Description = row.GetProperty("description").GetString()!,
                Category = row.GetProperty("category").GetString()!,
                LedgerCategory = row.GetProperty("ledgerCategory").GetString()!,
                Amount = row.GetProperty("amount").GetDecimal(),
            });
        }
        await context.SaveChangesAsync();

        var query = TransactionQueryService.ApplyAllFilters(
            context.Transactions.AsNoTracking(),
            useIlike: false,
            search: StringOrNull(criteria, "search"),
            searchMode: StringOrNull(criteria, "searchMode"),
            ledgerCategory: JoinOrNull(criteria, "buckets"),
            category: JoinOrNull(criteria, "categories"),
            txType: JoinOrNull(criteria, "txType"),
            startDate: StringOrNull(criteria, "startDate"),
            endDate: StringOrNull(criteria, "endDate"),
            minAmount: DecimalOrNull(criteria, "minAmount"),
            maxAmount: DecimalOrNull(criteria, "maxAmount"),
            recurringOnly: false,
            wishlistOnly: false,
            recurringFilter: null,
            wishlistFilter: null);

        var matched = await query.Select(transaction => transaction.Id).ToListAsync();
        var expectedMatching = expected.GetProperty("matchingIds")
            .EnumerateArray().Select(id => id.GetString()!).ToList();
        Assert.Equal(
            expectedMatching.OrderBy(id => id, StringComparer.Ordinal),
            matched.OrderBy(id => id, StringComparer.Ordinal));

        if (!expected.TryGetProperty("orderedIds", out var orderedIds)) return;

        var sort = input.TryGetProperty("sort", out var sortValue) ? sortValue.GetString() : "date-desc";
        var ordered = sort == "date-asc"
            ? matched.OrderBy(id => id, StringComparer.Ordinal)
            : matched.OrderByDescending(id => id, StringComparer.Ordinal);
        Assert.Equal(orderedIds.EnumerateArray().Select(id => id.GetString()!), ordered);
    }

    private static string? StringOrNull(JsonElement criteria, string name) =>
        criteria.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static decimal? DecimalOrNull(JsonElement criteria, string name) =>
        criteria.TryGetProperty(name, out var value) ? value.GetDecimal() : null;

    private static string? JoinOrNull(JsonElement criteria, string name) =>
        criteria.TryGetProperty(name, out var value)
            ? string.Join(',', value.EnumerateArray().Select(entry => entry.GetString()))
            : null;

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "ledger-filter-sort.cases.json");
}
