using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public sealed class MarketSeriesFetchPlannerTests
{
    private static readonly DateOnly Today = new(2046, 6, 15);
    private static readonly DateTime Now = new(2046, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);

    [Fact]
    public void FirstFetchAsksForTheNewestPageWhenTheHistoryIsLongerThanOneRequest()
    {
        var request = MarketSeriesFetchPlanner.Next(
            Today.AddYears(-30), new MarketSeriesState(null, null, null, null, null), Today, Now, Freshness, 4000);

        Assert.Equal(new MarketSeriesRequest(Today.AddDays(-3999), null), request);
    }

    [Fact]
    public void FirstFetchOfAShortHistoryStartsAtTheOldestNeededDate()
    {
        var request = MarketSeriesFetchPlanner.Next(
            Today.AddYears(-2), new MarketSeriesState(null, null, null, null, null), Today, Now, Freshness, 4000);

        Assert.Equal(new MarketSeriesRequest(Today.AddYears(-2), null), request);
    }

    [Fact]
    public void FreshSeriesCoveringTheNeedMakesNoRequest()
    {
        var state = new MarketSeriesState(Today.AddYears(-5), Today, Now.AddMinutes(-1), Today.AddYears(-5), Now.AddMinutes(-1));

        Assert.Null(MarketSeriesFetchPlanner.Next(Today.AddYears(-5), state, Today, Now, Freshness, 4000));
    }

    [Fact]
    public void AFreshSeriesStoredForAnotherUserStillBackfillsOlderHistoryThisUserNeeds()
    {
        // Another user's refresh left a fresh pair covering 2040 onward; this user's history starts in 2030.
        var coveredFrom = new DateOnly(2040, 1, 2);
        var state = new MarketSeriesState(coveredFrom, Today, Now.AddMinutes(-1), coveredFrom, Now.AddMinutes(-1));

        var request = MarketSeriesFetchPlanner.Next(new DateOnly(2030, 3, 1), state, Today, Now, Freshness, 4000);

        Assert.Equal(new MarketSeriesRequest(new DateOnly(2030, 3, 1), coveredFrom.AddDays(-1)), request);
    }

    [Fact]
    public void BackfillLongerThanOneRequestTakesTheNewestMissingPageFirst()
    {
        var coveredFrom = new DateOnly(2040, 1, 1);
        var state = new MarketSeriesState(coveredFrom, Today, Now, coveredFrom, Now);

        var request = MarketSeriesFetchPlanner.Next(new DateOnly(2010, 1, 1), state, Today, Now, Freshness, 100);

        Assert.Equal(new MarketSeriesRequest(coveredFrom.AddDays(-100), coveredFrom.AddDays(-1)), request);
    }

    [Fact]
    public void LegacySeriesWithoutACoverageMarkerTreatsAFirstBarWithinAWeekAsCovered()
    {
        // The first stored bar is the Monday after a weekend trade: nothing older exists to fetch.
        var state = new MarketSeriesState(Today.AddDays(-100), Today, Now, null, null);

        Assert.Null(MarketSeriesFetchPlanner.Next(Today.AddDays(-103), state, Today, Now, Freshness, 4000));
        Assert.False(MarketSeriesFetchPlanner.NeedsBackfill(Today.AddDays(-103), state));
        Assert.True(MarketSeriesFetchPlanner.NeedsBackfill(Today.AddDays(-120), state));
    }

    [Fact]
    public void StaleCoveredSeriesRefreshesForwardFromAWeekBeforeTheLatestBar()
    {
        var state = new MarketSeriesState(Today.AddYears(-1), Today.AddDays(-3), Now.AddDays(-3), Today.AddYears(-1), Now.AddDays(-3));

        var request = MarketSeriesFetchPlanner.Next(Today.AddYears(-1), state, Today, Now, Freshness, 4000);

        Assert.Equal(new MarketSeriesRequest(Today.AddDays(-10), null), request);
    }

    [Fact]
    public void SeriesTheProviderRecentlyHadNothingForIsNotAskedAgainUntilStale()
    {
        var state = new MarketSeriesState(null, null, null, Today.AddYears(-1), Now.AddMinutes(-1));

        Assert.Null(MarketSeriesFetchPlanner.Next(Today.AddYears(-1), state, Today, Now, Freshness, 4000));
    }

    [Fact]
    public void CoverageAfterARequestNeverMovesLater()
    {
        Assert.Equal(new DateOnly(2030, 1, 1), MarketSeriesFetchPlanner.CoveredFromAfter(
            new MarketSeriesRequest(new DateOnly(2030, 1, 1), new DateOnly(2039, 12, 31)),
            new MarketSeriesState(new DateOnly(2040, 1, 1), Today, Now, new DateOnly(2040, 1, 1), Now)));
        Assert.Equal(new DateOnly(2040, 1, 1), MarketSeriesFetchPlanner.CoveredFromAfter(
            new MarketSeriesRequest(Today.AddDays(-7), null),
            new MarketSeriesState(new DateOnly(2040, 1, 1), Today, Now, new DateOnly(2040, 1, 1), Now)));
    }
}
