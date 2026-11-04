using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

/// <summary>
/// Keeps the current user's investment month-end snapshots up to date and returns them. The rows
/// are a cache (see <see cref="InvestmentPeriodSnapshot"/>): this resumes from the newest usable
/// month, replays only what follows it, and falls back to a full replay whenever a stored row
/// cannot be trusted. It never guesses a missing price or rate.
/// </summary>
public sealed class InvestmentSnapshotService(
    AppDbContext context,
    InvestmentAccountingService accounting,
    IMarketDataProvider provider,
    FinancialClock clock,
    ILogger<InvestmentSnapshotService> logger)
{
    /// <summary>Bump when a stored figure's meaning changes: every row is then rebuilt.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// A month is stored only once its end is this far behind: a refresh re-reads the last week of
    /// bars, so a younger month-end price can still change underneath a stored value.
    /// </summary>
    public const int SettleDays = 8;

    private readonly MarketDataSeriesResolver seriesResolver = new(provider.Descriptor);

    public async Task<IReadOnlyList<InvestmentPeriodSnapshot>> EnsureAsync(CancellationToken cancellationToken)
    {
        var appCurrency = ((await context.FinancialSettings.AsNoTracking()
                .Select(value => value.Currency)
                .FirstOrDefaultAsync(cancellationToken)) ?? "USD")
            .ToUpperInvariant();
        var firstTrade = await context.InvestmentTransactions.AsNoTracking()
            .MinAsync(value => (DateOnly?)value.TradeDate, cancellationToken);
        var firstFlow = await context.InvestmentCashFlows.AsNoTracking()
            .MinAsync(value => (DateOnly?)value.Date, cancellationToken);
        var first = Min(firstTrade, firstFlow);
        var settled = clock.Today.AddDays(-SettleDays);
        var lastSettled = InvestmentPeriodCalculator.MonthEnd(settled) == settled
            ? settled
            : new DateOnly(settled.Year, settled.Month, 1).AddDays(-1);

        var stored = await context.InvestmentPeriodSnapshots
            .OrderBy(value => value.PeriodEnd).ThenBy(value => value.Segment)
            .ToListAsync(cancellationToken);
        if (first is null)
        {
            if (stored.Count > 0) await RemoveAsync(stored, cancellationToken);
            return [];
        }

        var monthEnds = InvestmentPeriodCalculator.MonthEndsBetween(first.Value, lastSettled);
        var usable = await UsablePrefixAsync(stored, monthEnds, appCurrency, cancellationToken);
        var resumeRow = usable.LastOrDefault(value => value.Segment == InvestmentSnapshotSegments.Total);
        var remaining = monthEnds.Where(value => resumeRow is null || value > resumeRow.PeriodEnd).ToList();
        if (remaining.Count == 0) return usable;

        var fingerprint = await SourceFingerprintAsync(cancellationToken);
        var computed = await ComputeAsync(appCurrency, resumeRow, usable, remaining, cancellationToken);
        if (computed is null) return usable;
        if (fingerprint != await SourceFingerprintAsync(cancellationToken))
        {
            // Activity changed while this ran; its own save already invalidated what it touched,
            // so storing figures computed from the older data would undo that. Serve, don't keep.
            return [.. usable, .. computed];
        }
        context.InvestmentPeriodSnapshots.AddRange(computed);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Another request stored the same months first; theirs are equally valid.
            logger.LogInformation(exception, "Concurrent investment snapshot rebuild; keeping the stored rows.");
            foreach (var row in computed) context.Entry(row).State = EntityState.Detached;
        }
        return [.. usable, .. computed];
    }

    /// <summary>
    /// The stored rows that can be trusted: same currency, provider and schema, contiguous from the first
    /// month, each month with a readable carry, and no incomplete month that newer market data may
    /// now complete. Everything after the first untrusted month is deleted.
    /// </summary>
    private async Task<List<InvestmentPeriodSnapshot>> UsablePrefixAsync(
        List<InvestmentPeriodSnapshot> stored,
        IReadOnlyList<DateOnly> monthEnds,
        string appCurrency,
        CancellationToken cancellationToken)
    {
        // Coverage is checked whenever the provider answers for any series; an incomplete month is
        // retried only after some answer arrived since it was computed.
        var lastAnswer = await context.MarketSeriesCoverages.AsNoTracking()
            .Where(value => value.Provider == provider.Descriptor.Id)
            .MaxAsync(value => (DateTime?)value.CheckedAt, cancellationToken);
        var byMonth = stored.GroupBy(value => value.PeriodEnd).ToDictionary(group => group.Key, group => group.ToList());
        var usable = new List<InvestmentPeriodSnapshot>();
        foreach (var monthEnd in monthEnds)
        {
            if (!byMonth.TryGetValue(monthEnd, out var rows)) break;
            var total = rows.FirstOrDefault(value => value.Segment == InvestmentSnapshotSegments.Total);
            var trusted = total is not null &&
                          rows.All(value => value.SchemaVersion == SchemaVersion &&
                                            value.Provider == provider.Descriptor.Id &&
                                            value.AppCurrency.Equals(appCurrency, StringComparison.OrdinalIgnoreCase)) &&
                          InvestmentCarryState.TryParse(total.CarryState) is not null &&
                          (total.IsComplete || lastAnswer is null || lastAnswer <= total.ComputedAt);
            if (!trusted) break;
            usable.AddRange(rows);
        }
        var stale = stored.Except(usable).ToList();
        if (stale.Count > 0) await RemoveAsync(stale, cancellationToken);
        return usable;
    }

    private async Task<List<InvestmentPeriodSnapshot>?> ComputeAsync(
        string appCurrency,
        InvestmentPeriodSnapshot? resumeRow,
        IReadOnlyList<InvestmentPeriodSnapshot> usable,
        IReadOnlyList<DateOnly> monthEnds,
        CancellationToken cancellationToken)
    {
        var openingDate = resumeRow?.PeriodEnd ?? DateOnly.MinValue;
        var carry = resumeRow is null ? InvestmentCarryState.Empty : InvestmentCarryState.TryParse(resumeRow.CarryState)!;
        var openingValues = usable.Where(value => value.PeriodEnd == openingDate)
            .ToDictionary(value => value.Segment, value => value.ClosingValue, StringComparer.Ordinal);

        var instruments = await context.InvestmentInstruments.AsNoTracking().ToDictionaryAsync(value => value.Id, cancellationToken);
        var transactions = await context.InvestmentTransactions.AsNoTracking()
            .Include(value => value.Instrument)
            .Where(value => value.TradeDate > openingDate)
            .ToListAsync(cancellationToken);
        var cashFlows = await context.InvestmentCashFlows.AsNoTracking()
            .Where(value => value.Date > openingDate)
            .ToListAsync(cancellationToken);

        // Market data only from a month before the resume point: every price or rate this walk
        // reads is dated after it, and month-end compaction keeps one bar per month for the gap.
        var barsFrom = openingDate == DateOnly.MinValue ? DateOnly.MinValue : openingDate.AddMonths(-1);
        var references = new Dictionary<Guid, MarketInstrumentReference>();
        foreach (var instrument in instruments.Values.Where(value => !value.IsCustom))
        {
            if (await MarketDataReferenceResolver.ResolveAsync(context, instrument, provider, cancellationToken) is { } reference)
                references[instrument.Id] = reference;
        }
        var externalIds = references.Values.Select(value => value.ExternalId).Distinct().ToList();
        var providerId = provider.Descriptor.Id;
        var priceBars = externalIds.Count == 0 ? [] : await context.MarketPriceBars.AsNoTracking()
            .Where(value => value.Provider == providerId && externalIds.Contains(value.ExternalInstrumentId) && value.MarketDate >= barsFrom)
            .ToListAsync(cancellationToken);
        var currencies = instruments.Values.Select(value => value.Currency)
            .Concat(carry.Cash.Select(value => value.Currency))
            .Concat(cashFlows.Select(value => value.Currency))
            .Concat(cashFlows.Where(value => value.ToCurrency is not null).Select(value => value.ToCurrency!))
            .Append("USD")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fxBars = await context.FxRateBars.AsNoTracking()
            .Where(value => value.Provider == providerId && value.MarketDate >= barsFrom &&
                            (currencies.Contains(value.BaseCurrency) || currencies.Contains(value.QuoteCurrency)))
            .ToListAsync(cancellationToken);

        var prices = instruments.Keys.ToDictionary(
            id => id,
            id => seriesResolver.ResolvePrices(references.GetValueOrDefault(id), priceBars));
        decimal? Price(Guid instrumentId, DateOnly date)
        {
            if (!prices.TryGetValue(instrumentId, out var series) || series.Count == 0) return null;
            var low = 0;
            var high = series.Count - 1;
            var found = -1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (series[middle].Date <= date) { found = middle; low = middle + 1; }
                else high = middle - 1;
            }
            return found < 0 ? null : series[found].Price;
        }
        var rates = new Dictionary<(string, DateOnly), decimal?>();
        decimal? Fx(string currency, DateOnly date)
        {
            if (!rates.TryGetValue((currency, date), out var rate))
                rates[(currency, date)] = rate = seriesResolver.ResolveFx(currency, appCurrency, date, fxBars)?.Rate;
            return rate;
        }

        IReadOnlyList<InvestmentPeriodResult> results;
        try
        {
            results = new InvestmentPeriodCalculator(accounting).Walk(
                appCurrency, carry, openingValues, openingDate, transactions, cashFlows, instruments, monthEnds, Price, Fx);
        }
        catch (InvestmentValidationException exception)
        {
            // History is validated on write, so this means stored activity no longer replays.
            // Store nothing rather than a figure built on a broken sequence.
            logger.LogWarning(exception, "Investment history could not be replayed for snapshots.");
            return null;
        }

        var now = DateTime.UtcNow;
        return results.SelectMany(result => result.Rows.Select(row => new InvestmentPeriodSnapshot
            {
                PeriodEnd = row.PeriodEnd,
                Segment = row.Segment,
                AppCurrency = appCurrency,
                Provider = provider.Descriptor.Id,
                SchemaVersion = SchemaVersion,
                OpeningValue = Round(row.OpeningValue),
                ClosingValue = Round(row.ClosingValue),
                MarketValue = Round(row.MarketValue),
                CashValue = Round(row.CashValue),
                NetContributions = Round(row.NetContributions),
                WeightedContributions = Round(row.WeightedContributions),
                Dividends = Round(row.Dividends),
                RealisedProfitLoss = Round(row.RealisedProfitLoss),
                CostBasis = Round(row.CostBasis),
                IsComplete = row.IsComplete,
                MissingReason = row.Missing.Count == 0 ? null : Truncate(string.Join(" ", row.Missing), 500),
                CarryState = row.Segment == InvestmentSnapshotSegments.Total ? result.Carry.Serialize() : null,
                ComputedAt = now,
            }))
            .ToList();
    }

    /// <summary>Changes whenever any activity or cash movement is added, edited, or removed.</summary>
    private async Task<(int, DateTime?, int, DateTime?)> SourceFingerprintAsync(CancellationToken cancellationToken)
        => (await context.InvestmentTransactions.CountAsync(cancellationToken),
            await context.InvestmentTransactions.MaxAsync(value => (DateTime?)value.UpdatedAt, cancellationToken),
            await context.InvestmentCashFlows.CountAsync(cancellationToken),
            await context.InvestmentCashFlows.MaxAsync(value => (DateTime?)value.UpdatedAt, cancellationToken));

    private async Task RemoveAsync(IReadOnlyCollection<InvestmentPeriodSnapshot> rows, CancellationToken cancellationToken)
    {
        context.InvestmentPeriodSnapshots.RemoveRange(rows);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static DateOnly? Min(DateOnly? left, DateOnly? right)
        => left is null ? right : right is null ? left : left < right ? left : right;

    // Ten places, like the bars these come from: a carried figure must not drift through rounding.
    private static decimal? Round(decimal? value)
        => value is null ? null : Math.Round(value.Value, 10, MidpointRounding.AwayFromZero);

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..(length - 1)] + "…";
}
