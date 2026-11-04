namespace FinancialAppApi.Services.Investments;

/// <summary>One provider request for a stored daily series. A null End means "up to today".</summary>
public sealed record MarketSeriesRequest(DateOnly Start, DateOnly? End)
{
    public bool IsBackfill => End is not null;
}

/// <summary>
/// What is stored for one daily series (an instrument's prices or a currency pair).
/// CoveredFrom is the oldest date the provider has been asked for; CheckedAt is when it was last asked.
/// Both are null for a series stored before coverage was recorded.
/// </summary>
public sealed record MarketSeriesState(
    DateOnly? EarliestStored,
    DateOnly? LatestStored,
    DateTime? LatestFetchedAt,
    DateOnly? CoveredFrom,
    DateTime? CheckedAt);

/// <summary>
/// Decides the next provider request for one stored daily series. Bars are a cache shared by
/// every user, so the cache must reach back to the oldest date the current user needs rather
/// than only moving forward from whatever another user stored: a currency switch, a backdated
/// entry, or a pair someone else keeps fresh would otherwise leave old years permanently unvalued.
/// Older history is fetched newest page first, never more than one bounded page per request, so a
/// decades-long history converges over a few refreshes whichever end a provider would truncate.
/// </summary>
public static class MarketSeriesFetchPlanner
{
    /// <summary>
    /// A series stored before coverage was recorded was fetched from the oldest trade date, and
    /// its first bar can sit a few days later (weekends, holidays). A need inside this window is
    /// treated as covered so the upgrade does not spend a provider call on every existing series.
    /// </summary>
    public const int LegacyCoverageToleranceDays = 7;

    private const int ForwardOverlapDays = 7;

    public static bool NeedsBackfill(DateOnly needFrom, MarketSeriesState state)
    {
        if (state.CoveredFrom is { } covered) return needFrom < covered;
        return state.EarliestStored is { } earliest && needFrom < earliest.AddDays(-LegacyCoverageToleranceDays);
    }

    public static MarketSeriesRequest? Next(
        DateOnly needFrom,
        MarketSeriesState state,
        DateOnly today,
        DateTime now,
        TimeSpan freshness,
        int maxDaysPerRequest)
    {
        if (NeedsBackfill(needFrom, state))
        {
            var end = (state.CoveredFrom ?? state.EarliestStored!.Value).AddDays(-1);
            return Bounded(needFrom, end, end, maxDaysPerRequest);
        }
        if (state.LatestStored is not { } latest)
        {
            // The provider was asked recently and had nothing: wait for the series to go stale.
            if (state.CheckedAt is { } checkedAt && now - checkedAt < freshness) return null;
            return Bounded(needFrom, null, today, maxDaysPerRequest);
        }
        if (state.LatestFetchedAt is { } fetchedAt && now - fetchedAt < freshness) return null;
        return new MarketSeriesRequest(latest.AddDays(-ForwardOverlapDays), null);
    }

    /// <summary>The coverage to record once a request has been answered, even with no bars.</summary>
    public static DateOnly CoveredFromAfter(MarketSeriesRequest request, MarketSeriesState state)
    {
        var previous = state.CoveredFrom ?? state.EarliestStored;
        return previous is { } value && value < request.Start ? value : request.Start;
    }

    private static MarketSeriesRequest Bounded(DateOnly start, DateOnly? end, DateOnly pageEnd, int maxDays)
    {
        var span = (long)pageEnd.DayNumber - start.DayNumber + 1;
        return span > Math.Max(1, maxDays)
            ? new MarketSeriesRequest(pageEnd.AddDays(-(Math.Max(1, maxDays) - 1)), end)
            : new MarketSeriesRequest(start, end);
    }
}
