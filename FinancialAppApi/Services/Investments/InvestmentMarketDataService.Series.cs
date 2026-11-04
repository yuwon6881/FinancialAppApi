using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

/// <summary>
/// Fetching one stored daily series: an instrument's prices or a currency pair. Each call makes at
/// most one provider request, chosen by <see cref="MarketSeriesFetchPlanner"/>; a series that needs
/// more (older pages, or a forward refresh after a backfill) asks to be queued again.
/// </summary>
public sealed partial class InvestmentMarketDataService
{
    private sealed record SeriesRefreshOutcome(string? Warning, bool NeedsAnotherCall);

    private TimeSpan Freshness => TimeSpan.FromMinutes(Math.Max(1, _options.FreshnessMinutes));

    internal static string FxSeriesKey(string baseCurrency, string quoteCurrency)
        => $"{baseCurrency.ToUpperInvariant()}/{quoteCurrency.ToUpperInvariant()}";

    private async Task<SeriesRefreshOutcome> RefreshInstrumentAsync(
        InvestmentInstrument instrument,
        IReadOnlyList<InvestmentTransaction> transactions,
        CancellationToken cancellationToken)
    {
        var reference = await MarketDataReferenceResolver.ResolveAsync(
            context, instrument, provider, cancellationToken);
        if (reference is null)
            return new($"No market-data mapping is available for {instrument.Symbol}.", false);
        var bars = context.MarketPriceBars.Where(value =>
            value.Provider == ProviderId && value.ExternalInstrumentId == reference.ExternalId);
        var latest = await bars.OrderByDescending(value => value.MarketDate).FirstOrDefaultAsync(cancellationToken);
        var earliest = await bars.OrderBy(value => value.MarketDate)
            .Select(value => (DateOnly?)value.MarketDate).FirstOrDefaultAsync(cancellationToken);
        var coverage = await LoadCoverageAsync(MarketSeriesKinds.Price, reference.ExternalId, cancellationToken);
        var state = new MarketSeriesState(earliest, latest?.MarketDate, latest?.FetchedAt, coverage?.CoveredFrom, coverage?.CheckedAt);
        var needFrom = transactions.Where(value => value.InstrumentId == instrument.Id).Min(value => value.TradeDate);
        var request = MarketSeriesFetchPlanner.Next(
            needFrom, state, Today, DateTime.UtcNow, Freshness, provider.MaxSeriesDaysPerRequest);
        if (request is null) return new(null, false);

        var fetched = await provider.GetDailySeriesAsync(reference, request.Start, request.End, cancellationToken);
        string? warning = null;
        if (fetched.Count == 0)
        {
            // Delisted symbol, market holiday, wrong mapping, or no history that old. Record the
            // attempt anyway; otherwise the freshness gate never advances and this symbol
            // re-burns a provider call on every refresh, forever.
            if (latest is not null && !request.IsBackfill) latest.FetchedAt = DateTime.UtcNow;
            logger.LogInformation("Provider returned no price bars for {Symbol}.", instrument.Symbol);
            if (!request.IsBackfill)
                warning = $"No recent prices were available for {instrument.Symbol}. Check the symbol mapping if this persists.";
        }
        else
        {
            // One range read instead of a lookup per bar: a first refresh of a long-held position
            // returns years of daily bars. Keyed by date, so a repeated date in the provider's
            // series updates the row it already added instead of inserting a duplicate.
            var firstDate = fetched.Min(bar => bar.Date);
            var lastDate = fetched.Max(bar => bar.Date);
            var existingByDate = await bars
                .Where(value => value.MarketDate >= firstDate && value.MarketDate <= lastDate)
                .ToDictionaryAsync(value => value.MarketDate, cancellationToken);
            foreach (var bar in fetched)
            {
                if (!existingByDate.TryGetValue(bar.Date, out var existing))
                {
                    existingByDate[bar.Date] = context.MarketPriceBars.Add(new MarketPriceBar
                    {
                        Provider = ProviderId,
                        ExternalInstrumentId = reference.ExternalId,
                        Symbol = instrument.ProviderSymbol ?? instrument.Symbol,
                        Mic = instrument.ProviderMic ?? "",
                        MarketDate = bar.Date,
                        Close = bar.Close
                    }).Entity;
                }
                else
                {
                    existing.Close = bar.Close;
                    existing.FetchedAt = DateTime.UtcNow;
                }
            }
        }
        return new(warning, RecordAnswer(MarketSeriesKinds.Price, reference.ExternalId, coverage, state, request, fetched.Select(bar => bar.Date), needFrom));
    }

    private async Task<SeriesRefreshOutcome> RefreshFxAsync(
        string baseCurrency,
        string quoteCurrency,
        DateOnly needFrom,
        CancellationToken cancellationToken)
    {
        var seriesKey = FxSeriesKey(baseCurrency, quoteCurrency);
        var bars = context.FxRateBars.Where(value =>
            value.Provider == ProviderId &&
            value.BaseCurrency == baseCurrency &&
            value.QuoteCurrency == quoteCurrency);
        var latest = await bars.OrderByDescending(value => value.MarketDate).FirstOrDefaultAsync(cancellationToken);
        var earliest = await bars.OrderBy(value => value.MarketDate)
            .Select(value => (DateOnly?)value.MarketDate).FirstOrDefaultAsync(cancellationToken);
        var coverage = await LoadCoverageAsync(MarketSeriesKinds.Fx, seriesKey, cancellationToken);
        var state = new MarketSeriesState(earliest, latest?.MarketDate, latest?.FetchedAt, coverage?.CoveredFrom, coverage?.CheckedAt);
        var request = MarketSeriesFetchPlanner.Next(
            needFrom, state, Today, DateTime.UtcNow, Freshness, provider.MaxSeriesDaysPerRequest);
        if (request is null) return new(null, false);

        var fetched = await provider.GetFxSeriesAsync(baseCurrency, quoteCurrency, request.Start, request.End, cancellationToken);
        string? warning = null;
        if (fetched.Count == 0)
        {
            // See RefreshInstrumentAsync: record the attempt so the freshness gate advances
            // even when the provider has nothing for this pair.
            if (latest is not null && !request.IsBackfill) latest.FetchedAt = DateTime.UtcNow;
            logger.LogInformation("Provider returned no FX bars for {Base}/{Quote}.", baseCurrency, quoteCurrency);
            if (!request.IsBackfill)
                warning = $"No recent {baseCurrency}/{quoteCurrency} exchange rates were available.";
        }
        else
        {
            // Same single range read as RefreshInstrumentAsync.
            var firstDate = fetched.Min(bar => bar.Date);
            var lastDate = fetched.Max(bar => bar.Date);
            var existingByDate = await bars
                .Where(value => value.MarketDate >= firstDate && value.MarketDate <= lastDate)
                .ToDictionaryAsync(value => value.MarketDate, cancellationToken);
            foreach (var bar in fetched)
            {
                if (!existingByDate.TryGetValue(bar.Date, out var existing))
                {
                    existingByDate[bar.Date] = context.FxRateBars.Add(new FxRateBar
                    {
                        Provider = ProviderId,
                        BaseCurrency = baseCurrency,
                        QuoteCurrency = quoteCurrency,
                        MarketDate = bar.Date,
                        Rate = bar.Rate
                    }).Entity;
                }
                else
                {
                    existing.Rate = bar.Rate;
                    existing.FetchedAt = DateTime.UtcNow;
                }
            }
        }
        return new(warning, RecordAnswer(MarketSeriesKinds.Fx, seriesKey, coverage, state, request, fetched.Select(bar => bar.Date), needFrom));
    }

    private Task<MarketSeriesCoverage?> LoadCoverageAsync(string kind, string seriesKey, CancellationToken cancellationToken)
        => context.MarketSeriesCoverages.FirstOrDefaultAsync(value =>
            value.Provider == ProviderId && value.Kind == kind && value.SeriesKey == seriesKey, cancellationToken);

    /// <summary>Records how far back the provider has now been asked; returns whether the series still needs another call.</summary>
    private bool RecordAnswer(
        string kind,
        string seriesKey,
        MarketSeriesCoverage? coverage,
        MarketSeriesState state,
        MarketSeriesRequest request,
        IEnumerable<DateOnly> returnedDates,
        DateOnly needFrom)
    {
        var now = DateTime.UtcNow;
        var coveredFrom = MarketSeriesFetchPlanner.CoveredFromAfter(request, state);
        if (coverage is null)
        {
            context.MarketSeriesCoverages.Add(new MarketSeriesCoverage
            {
                Provider = ProviderId,
                Kind = kind,
                SeriesKey = seriesKey,
                CoveredFrom = coveredFrom,
                CheckedAt = now
            });
        }
        else
        {
            coverage.CoveredFrom = coveredFrom;
            coverage.CheckedAt = now;
        }

        var dates = returnedDates.ToList();
        var latestStored = dates.Count == 0
            ? state.LatestStored
            : state.LatestStored is { } stored && stored > dates.Max() ? stored : dates.Max();
        var earliestStored = dates.Count == 0
            ? state.EarliestStored
            : state.EarliestStored is { } first && first < dates.Min() ? first : dates.Min();
        // A backfill leaves the newest bar's freshness as it was; a forward request refreshed it.
        var latestFetchedAt = request.IsBackfill
            ? state.LatestFetchedAt
            : latestStored is null ? null : now;
        var answered = new MarketSeriesState(earliestStored, latestStored, latestFetchedAt, coveredFrom, now);
        return MarketSeriesFetchPlanner.Next(
            needFrom, answered, Today, now, Freshness, provider.MaxSeriesDaysPerRequest) is not null;
    }

    /// <summary>
    /// Whether any series this user needs reaches back less far than their oldest dated entry.
    /// Lets the freshness gate admit a refresh for a pair another user keeps fresh.
    /// </summary>
    private async Task<bool> AnySeriesNeedsBackfillAsync(
        IEnumerable<(string Kind, string SeriesKey, DateOnly NeedFrom)> series,
        CancellationToken cancellationToken)
    {
        foreach (var (kind, seriesKey, needFrom) in series)
        {
            var coverage = await context.MarketSeriesCoverages.AsNoTracking()
                .Where(value => value.Provider == ProviderId && value.Kind == kind && value.SeriesKey == seriesKey)
                .Select(value => (DateOnly?)value.CoveredFrom)
                .FirstOrDefaultAsync(cancellationToken);
            DateOnly? earliest = null;
            if (coverage is null && kind == MarketSeriesKinds.Price)
            {
                earliest = await context.MarketPriceBars.AsNoTracking()
                    .Where(value => value.Provider == ProviderId && value.ExternalInstrumentId == seriesKey)
                    .MinAsync(value => (DateOnly?)value.MarketDate, cancellationToken);
            }
            else if (coverage is null)
            {
                var pair = seriesKey.Split('/');
                earliest = await context.FxRateBars.AsNoTracking()
                    .Where(value => value.Provider == ProviderId &&
                                    value.BaseCurrency == pair[0] &&
                                    value.QuoteCurrency == pair[1])
                    .MinAsync(value => (DateOnly?)value.MarketDate, cancellationToken);
            }
            if (MarketSeriesFetchPlanner.NeedsBackfill(needFrom, new MarketSeriesState(earliest, null, null, coverage, null)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The oldest date a currency must be converted from: its funds' trades and every cash
    /// movement in or out of it, including the bought side of a conversion. Today when unused.
    /// </summary>
    internal static DateOnly FxNeedFrom(
        string currency,
        IEnumerable<(Guid InstrumentId, DateOnly TradeDate)> trades,
        IReadOnlyDictionary<Guid, string> instrumentCurrencies,
        IEnumerable<(string Currency, string? ToCurrency, DateOnly Date)> cashFlows)
    {
        var dates = trades
            .Where(trade => instrumentCurrencies.TryGetValue(trade.InstrumentId, out var value) &&
                            value.Equals(currency, StringComparison.OrdinalIgnoreCase))
            .Select(trade => trade.TradeDate)
            .Concat(cashFlows
                .Where(flow => flow.Currency.Equals(currency, StringComparison.OrdinalIgnoreCase) ||
                               (flow.ToCurrency?.Equals(currency, StringComparison.OrdinalIgnoreCase) ?? false))
                .Select(flow => flow.Date))
            .ToList();
        return dates.Count == 0 ? Today : dates.Min();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
}
