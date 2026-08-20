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

    // "hair cut" typed against a saved "Haircut" matched neither the exact ILIKE nor pg_trgm's
    // strict-word floor, so the assistant reported an empty ledger over two visible rows.
    [Theory]
    [InlineData("hair cut", "Haircut", true)]
    [InlineData("haircut", "Hair Cut", true)]
    [InlineData("top up", "Top-up TNG", true)]
    [InlineData("haircut", "Spotify subscription", false)]
    [InlineData("cut", "Haircut", false)]
    public void IsCompactMatch_IgnoresSpacingInBothDirections(string search, string description, bool expected)
    {
        Assert.Equal(expected, TransactionTextSearch.IsCompactMatch(search, description));
        Assert.False(TransactionTextSearch.IsFuzzyMatch("hair cut", "Haircut"),
            "the compact rung exists precisely because the trigram pass rejects this pair");
    }

    [Fact]
    public void ApplyCompact_StripsSeparatorsOnTheColumnAsWellAsTheTerm()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var context = new AppDbContext(options);
        context.SetCurrentUser(TestHelpers.DefaultUserId);

        var sql = TransactionTextSearch.ApplyCompact(context.Transactions.AsNoTracking(), "hair cut")
            .ToQueryString();

        Assert.Contains("replace", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("haircut", sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(TransactionTextSearch.CanApplyCompact("tv"));
        Assert.True(TransactionTextSearch.CanApplyCompact("hair cut"));
    }

    [Fact]
    public void BuildPurchaseFrequencyMetric_ProjectsTheNextExpectedDateFromTheMedianGap()
    {
        var matches = new[]
        {
            new AiAssistantService.AiPurchaseMatch("one", new DateOnly(2026, 6, 23), "Haircut"),
            new AiAssistantService.AiPurchaseMatch("two", new DateOnly(2026, 7, 14), "Haircut")
        };

        var metric = AiAssistantService.BuildPurchaseFrequencyMetric(
            "hair cut",
            "spacing",
            matches,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 8, 21),
            new DateOnly(2026, 8, 21),
            cycleDay: 1);

        Assert.Equal("spacing", metric.MatchMode);
        Assert.Equal(21m, metric.MedianGapDays);
        Assert.Equal("about every 3 weeks", metric.TypicalCadence);
        Assert.Equal("2026-08-04", metric.NextExpectedDate);
        Assert.Equal(-17, metric.DaysUntilNextExpected);
        Assert.True(metric.NextExpectedIsOverdue);
    }

    [Fact]
    public void BuildPurchaseFrequencyMetric_NextExpectedIsAbsentWithoutTwoPurchaseDays()
    {
        var metric = AiAssistantService.BuildPurchaseFrequencyMetric(
            "hair cut", "exact",
            [new AiAssistantService.AiPurchaseMatch("one", new DateOnly(2026, 7, 14), "Haircut")],
            new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 21), new DateOnly(2026, 8, 21), cycleDay: 1);

        Assert.Null(metric.NextExpectedDate);
        Assert.Null(metric.DaysUntilNextExpected);
        Assert.False(metric.NextExpectedIsOverdue);
    }
}
