using FinancialAppApi.Database;
using FinancialAppApi.Services;
using FinancialAppApi.Services.AI;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests.AiTools;

// The tool tests run on InMemory, which silently evaluates anything EF cannot translate. These
// compile the assistant's transaction queries against the real Npgsql provider -- ToQueryString
// throws on an untranslatable tree -- without opening a connection.
public class AiTransactionQuerySqlTranslationTests
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

    private static readonly AiTransactionFilter EveryFilter = new(
        [
            AiDateRange.FromDates(new DateOnly(2026, 1, 25), new DateOnly(2026, 2, 24)),
            AiDateRange.FromDates(new DateOnly(2026, 5, 25), new DateOnly(2026, 6, 24))
        ],
        Category: "Food",
        LedgerCategory: "Income",
        TxType: "income",
        MinAmount: 10m,
        MaxAmount: 500m,
        AccountId: "acct-essentials");

    [Theory]
    [InlineData("newest")]
    [InlineData("oldest")]
    [InlineData("largest")]
    [InlineData("smallest")]
    public void EveryFilterAndSortTranslates(string sort)
    {
        using var context = NewNpgsqlContext();
        var service = new AiTransactionQueryService(context);

        var sql = AiTransactionQueryService.Order(service.Scoped(EveryFilter), sort).Take(10).ToQueryString();

        Assert.Contains("\"Date\" >=", sql);
        Assert.Contains(" OR ", sql);
    }

    [Theory]
    [InlineData("outflow")]
    [InlineData("inflow")]
    [InlineData("transfer")]
    public void TransactionTypesTranslate(string txType)
    {
        using var context = NewNpgsqlContext();
        var service = new AiTransactionQueryService(context);

        var sql = service.Scoped(EveryFilter with { TxType = txType, LedgerCategory = null }).ToQueryString();

        Assert.NotEmpty(sql);
    }

    [Fact]
    public void EveryMatchRungTranslatesOverTheScopedQuery()
    {
        using var context = NewNpgsqlContext();
        var scoped = new AiTransactionQueryService(context).Scoped(EveryFilter);

        Assert.Contains("ILIKE", TransactionTextSearch.Apply(scoped, true, "hair cut", "contains").ToQueryString());
        Assert.Contains("~*", TransactionTextSearch.Apply(scoped, true, "hair cut", "whole-word").ToQueryString());
        Assert.Contains("replace", TransactionTextSearch.ApplyCompact(scoped, "hair cut").ToQueryString());
        Assert.Contains("strict_word_similarity", TransactionTextSearch.ApplyFuzzy(scoped, "hair cut").ToQueryString());
    }

    [Fact]
    public void AggregatesTranslate()
    {
        using var context = NewNpgsqlContext();
        var scoped = new AiTransactionQueryService(context).Scoped(EveryFilter with { TxType = null });
        var reportable = AiTransactionQueryService.Reportable(scoped);

        // Aggregate operators execute immediately, so compile their projections as subqueries instead.
        var sql = scoped
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                First = group.Min(t => (DateTime?)t.Date),
                Last = group.Max(t => (DateTime?)t.Date)
            })
            .ToQueryString();
        var outflowSql = reportable.Where(t => t.Amount < 0).GroupBy(_ => 1).Select(g => g.Sum(t => (decimal?)t.Amount)).ToQueryString();

        Assert.Contains("count", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accountmove", outflowSql);
    }
}
