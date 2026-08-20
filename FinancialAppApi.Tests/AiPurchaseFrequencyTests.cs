using FinancialAppApi.Database;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class AiPurchaseFrequencyTests
{
    [Fact]
    public void BuildPurchaseFrequencyMetric_UsesDistinctDaysMedianAndCustomCycles()
    {
        var matches = new[]
        {
            new AiAssistantService.AiPurchaseMatch("one", new DateOnly(2026, 1, 28), "Deodorant"),
            new AiAssistantService.AiPurchaseMatch("same-day", new DateOnly(2026, 1, 28), "Deodorant refill"),
            new AiAssistantService.AiPurchaseMatch("two", new DateOnly(2026, 2, 27), "Deodorant"),
            new AiAssistantService.AiPurchaseMatch("three", new DateOnly(2026, 4, 8), "Deodorant")
        };

        var metric = AiAssistantService.BuildPurchaseFrequencyMetric(
            "deodorant",
            "exact",
            matches,
            new DateOnly(2026, 1, 28),
            new DateOnly(2026, 4, 27),
            new DateOnly(2026, 4, 27),
            cycleDay: 28);

        Assert.Equal(4, metric.TransactionCount);
        Assert.Equal(3, metric.PurchaseDayCount);
        Assert.Equal(35m, metric.MedianGapDays);
        Assert.Equal("about every 5 weeks", metric.TypicalCadence);
        Assert.Equal(3, metric.ObservedCycleCount);
        Assert.Equal(1m, metric.PurchaseDaysPerCycle);
        Assert.Equal(19, metric.DaysSinceLastPurchase);
    }

    [Fact]
    public void BuildPurchaseFrequencyMetric_ZeroAndOnePurchaseDoNotInventCadence()
    {
        var none = AiAssistantService.BuildPurchaseFrequencyMetric(
            "deodorant", "none", [], new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31),
            new DateOnly(2026, 3, 31), cycleDay: 1);
        var one = AiAssistantService.BuildPurchaseFrequencyMetric(
            "deodorant", "exact",
            [new AiAssistantService.AiPurchaseMatch("one", new DateOnly(2026, 2, 1), "Deodorant")],
            new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), new DateOnly(2026, 3, 31), cycleDay: 1);

        Assert.Equal("none", none.Confidence);
        Assert.Null(none.TypicalCadence);
        Assert.Equal("insufficient", one.Confidence);
        Assert.Null(one.MedianGapDays);
        Assert.Null(one.TypicalCadence);
    }

    [Theory]
    [InlineData("deoderant", "Watsons deodorant", true)]
    [InlineData("toothbrsh", "Electric toothbrush", true)]
    [InlineData("soap", "Spotify subscription", false)]
    public void IsFuzzyMatch_IsTypoTolerantWithoutSemanticGuessing(
        string search,
        string description,
        bool expected)
    {
        Assert.Equal(expected, TransactionTextSearch.IsFuzzyMatch(search, description));
    }

    [Fact]
    public void ApplyFuzzy_UsesParameterizedPostgresStrictWordSimilarity()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var context = new AppDbContext(options);
        context.SetCurrentUser(TestHelpers.DefaultUserId);

        var sql = TransactionTextSearch.ApplyFuzzy(context.Transactions.AsNoTracking(), "deoderant")
            .ToQueryString();

        Assert.Contains("strict_word_similarity", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0.70d, TransactionTextSearch.FuzzyThreshold);
        Assert.Contains("term", sql, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("-- @", sql, StringComparison.Ordinal);
    }
}
