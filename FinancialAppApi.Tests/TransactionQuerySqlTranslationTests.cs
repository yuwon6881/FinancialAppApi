using FinancialAppApi.Database;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

/// <summary>
/// Compiles every ledger filter and sort against the real Npgsql provider.
///
/// The rest of the suite runs on the InMemory provider, where <c>IsNpgsql()</c> is false and any
/// clause EF cannot translate is silently evaluated in LINQ-to-Objects instead. That means no
/// other test can catch an untranslatable predicate — the ILIKE search, the folded bucket chain
/// and the collated tie-breaker would all fail for the first time in production. ToQueryString()
/// throws on an untranslatable tree, so compiling here is the check. No connection is opened.
/// </summary>
public class TransactionQuerySqlTranslationTests
{
    private static AppDbContext NewNpgsqlContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=translation-only;Username=none;Password=none")
            .Options;
        var context = new AppDbContext(options);
        context.SetCurrentUser(TestHelpers.DefaultUserId);
        return context;
    }

    private static string Sql(
        string? search = null,
        string? searchMode = null,
        string? ledgerCategory = null,
        string? category = null,
        string? txType = null,
        string? startDate = null,
        string? endDate = null,
        decimal? minAmount = null,
        decimal? maxAmount = null,
        string? recurringFilter = null,
        string? wishlistFilter = null,
        string? reloadFilter = null,
        string? accountId = null,
        string? sort = null)
    {
        using var context = NewNpgsqlContext();
        var query = TransactionQueryService.ApplyAllFilters(
            context.Transactions.AsNoTracking(),
            useIlike: true,
            search,
            searchMode,
            ledgerCategory,
            category,
            txType,
            startDate,
            endDate,
            minAmount,
            maxAmount,
            recurringOnly: false,
            wishlistOnly: false,
            recurringFilter,
            wishlistFilter,
            reloadFilter,
            accountId);
        return TransactionQueryService.ApplySort(query, sort, useOrdinalCollation: true).ToQueryString();
    }

    [Fact]
    public void BaselineExcludesDiscardedCaseInsensitively()
    {
        var sql = Sql();

        Assert.Contains("lower(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("discarded", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("exact")]
    [InlineData("whole-word")]
    public void EverySearchModeTranslates(string? mode)
    {
        var sql = Sql(search: "coffee 50% _off", searchMode: mode);

        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainsSearchUsesIlikeRatherThanClientEvaluation()
    {
        var sql = Sql(search: "coffee");

        Assert.Contains("ILIKE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Stability")]
    [InlineData("stability")]
    [InlineData("Income")]
    [InlineData("Essentials,Growth,Rewards")]
    public void FoldedBucketChainTranslates(string buckets)
    {
        // The folded OR chain replaced `list.Any(bucket => ...)`, whose translation depended on
        // primitive-collection support the InMemory provider never exercises.
        var sql = Sql(ledgerCategory: buckets);

        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Any(", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("inflow")]
    [InlineData("outflow")]
    [InlineData("transfer")]
    [InlineData("inflow,outflow")]
    [InlineData("inflow,transfer")]
    [InlineData("outflow,transfer")]
    [InlineData("inflow,outflow,transfer")]
    [InlineData("bogus")]
    public void EveryTransactionTypeCombinationTranslates(string txType)
    {
        var sql = Sql(txType: txType);

        Assert.False(string.IsNullOrWhiteSpace(sql));
    }

    [Fact]
    public void StructuralTypeFilterNamesAccountMove()
    {
        var sql = Sql(txType: "transfer");

        Assert.Contains("accountmove", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("date-asc")]
    [InlineData("amount-asc")]
    [InlineData("amount-desc")]
    [InlineData("unrecognized")]
    public void EverySortCollatesTheIdTieBreakerOrdinally(string? sort)
    {
        var sql = Sql(sort: sort);

        Assert.Contains("COLLATE \"C\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainSortOmitsTheCollationWhenNotOnPostgres()
    {
        using var context = NewNpgsqlContext();

        var sql = TransactionQueryService
            .ApplySort(context.Transactions.AsNoTracking(), "date-asc", useOrdinalCollation: false)
            .ToQueryString();

        Assert.DoesNotContain("COLLATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RemainingFiltersTranslate()
    {
        var sql = Sql(
            category: "Food,Transport",
            startDate: "2026-01-01",
            endDate: "2026-12-31",
            minAmount: 10m,
            maxAmount: 500m,
            recurringFilter: "exclude",
            wishlistFilter: "only",
            reloadFilter: "put-back",
            accountId: "acct-a,acct-b");

        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
    }
}
