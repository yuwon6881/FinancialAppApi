using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Services.Investments;

public sealed record MarketDataBackfillResult(
    string ProviderId,
    int CallsUsed,
    int MappingsCreated,
    int PriceSeriesUpdated,
    int FxSeriesUpdated,
    IReadOnlyList<string> Warnings);

public sealed record MarketDataCutoverReport(
    string ActiveProviderId,
    string CandidateProviderId,
    int RequiredInstruments,
    int MissingMappings,
    int MissingOrStalePriceSeries,
    int MissingOrStaleFxSeries,
    int IncompleteRefreshJobs,
    int PortfoliosCompared,
    int PortfolioDifferencesAboveTolerance,
    decimal TolerancePercent,
    int RollbackRetentionDays,
    bool DifferencesApproved,
    bool Ready,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Operator-only shadow population and cutover validation. It is intentionally exposed only
/// through Program's command-line switches: normal requests use exactly one active provider.
/// </summary>
public sealed class MarketDataCutoverService(
    AppDbContext context,
    MarketDataProviderRegistry registry,
    IOptions<MarketDataOptions> options,
    ILogger<MarketDataCutoverService> logger)
{
    private readonly MarketDataOptions _options = options.Value;

    public async Task<MarketDataBackfillResult> BackfillAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var provider = registry.GetRequired(providerId);
        if (!provider.Descriptor.IsConfigured)
            throw new InvalidOperationException($"Market-data provider '{providerId}' is not configured.");

        var calls = 0;
        var quotaDenied = false;
        var quota = new MarketDataQuotaService(context, provider);
        var mappingsCreated = 0;
        var pricesUpdated = 0;
        var fxUpdated = 0;
        var warnings = new List<string>();
        var instruments = await RequiredInstrumentsAsync(cancellationToken);
        var mappings = await context.InvestmentInstrumentMarketMappings.IgnoreQueryFilters()
            .Where(value => value.ProviderId == providerId)
            .ToListAsync(cancellationToken);

        foreach (var required in instruments)
        {
            var mapping = mappings.FirstOrDefault(value => value.InvestmentInstrumentId == required.Instrument.Id);
            if (mapping is null)
            {
                if (!await ReserveCallAsync()) break;
                calls++;
                var candidates = await provider.SearchAsync(required.Instrument.Symbol, cancellationToken);
                var matches = candidates.Where(value =>
                        value.Availability != MarketInstrumentAvailability.Unavailable &&
                        value.Symbol.Equals(required.Instrument.Symbol, StringComparison.OrdinalIgnoreCase) &&
                        MicMatches(required.Instrument, value.Mic))
                    .ToList();
                if (matches.Count != 1)
                {
                    warnings.Add(matches.Count == 0
                        ? $"No unambiguous {provider.Descriptor.DisplayName} mapping was found for {required.Instrument.Symbol}."
                        : $"More than one {provider.Descriptor.DisplayName} mapping matched {required.Instrument.Symbol}.");
                    continue;
                }
                var match = matches[0];
                mapping = new InvestmentInstrumentMarketMapping
                {
                    UserId = required.Instrument.UserId,
                    InvestmentInstrumentId = required.Instrument.Id,
                    ProviderId = providerId,
                    ExternalInstrumentId = match.MarketDataReference.ExternalId,
                    DisplaySymbol = match.Symbol,
                    DisplayMic = match.Mic
                };
                context.InvestmentInstrumentMarketMappings.Add(mapping);
                mappings.Add(mapping);
                mappingsCreated++;
            }

            if (quotaDenied) break;
            if (await HasRequiredPriceCoverageAsync(
                    providerId, mapping.ExternalInstrumentId, required.EarliestTradeDate, cancellationToken))
                continue;
            if (!await ReserveCallAsync()) break;
            calls++;
            var bars = await provider.GetDailySeriesAsync(
                new MarketInstrumentReference(providerId, mapping.ExternalInstrumentId),
                required.EarliestTradeDate,
                cancellationToken);
            await UpsertPricesAsync(providerId, mapping, bars, cancellationToken);
            if (bars.Count > 0) pricesUpdated++;
        }

        foreach (var pair in await RequiredFxPairsAsync(cancellationToken))
        {
            if (quotaDenied) break;
            if (await HasRequiredFxCoverageAsync(
                    providerId, pair.BaseCurrency, pair.QuoteCurrency, pair.EarliestDate, cancellationToken))
                continue;
            if (!await ReserveCallAsync()) break;
            calls++;
            var bars = await provider.GetFxSeriesAsync(
                pair.BaseCurrency, pair.QuoteCurrency, pair.EarliestDate, cancellationToken);
            await UpsertFxAsync(providerId, pair.BaseCurrency, pair.QuoteCurrency, bars, cancellationToken);
            if (bars.Count > 0) fxUpdated++;
        }

        await context.SaveChangesAsync(cancellationToken);
        if (quotaDenied)
            warnings.Add("The provider minute allowance was reached. Run the backfill command again after the quota window resets.");
        logger.LogInformation(
            "Shadow market-data backfill for {ProviderId} used {Calls} calls and created {Mappings} mappings.",
            providerId, calls, mappingsCreated);
        return new MarketDataBackfillResult(providerId, calls, mappingsCreated, pricesUpdated, fxUpdated, warnings);

        async Task<bool> ReserveCallAsync()
        {
            if (await quota.ReserveOperatorAsync(1, cancellationToken) > 0) return true;
            quotaDenied = true;
            return false;
        }
    }

    public async Task<MarketDataCutoverReport> ValidateAsync(
        string candidateProviderId,
        bool approveDifferences,
        CancellationToken cancellationToken)
    {
        var active = registry.ActiveProvider;
        var candidate = registry.GetRequired(candidateProviderId);
        var required = await RequiredInstrumentsAsync(cancellationToken);
        var mappings = await context.InvestmentInstrumentMarketMappings.IgnoreQueryFilters().AsNoTracking()
            .Where(value => value.ProviderId == candidateProviderId)
            .ToListAsync(cancellationToken);
        var missingMappings = required.Count(value =>
            mappings.All(mapping => mapping.InvestmentInstrumentId != value.Instrument.Id));
        var stalePrices = 0;
        foreach (var mapping in mappings.Where(value =>
                     required.Any(item => item.Instrument.Id == value.InvestmentInstrumentId)))
        {
            var earliest = required.Single(value => value.Instrument.Id == mapping.InvestmentInstrumentId).EarliestTradeDate;
            if (!await HasRequiredPriceCoverageAsync(
                    candidateProviderId, mapping.ExternalInstrumentId, earliest, cancellationToken))
                stalePrices++;
        }

        var pairs = await RequiredFxPairsAsync(cancellationToken);
        var staleFx = 0;
        foreach (var pair in pairs)
        {
            if (!await HasRequiredFxCoverageAsync(
                    candidateProviderId, pair.BaseCurrency, pair.QuoteCurrency, pair.EarliestDate, cancellationToken))
                staleFx++;
        }

        var incompleteJobs = await context.MarketDataRefreshJobs.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(value => value.ProviderId == candidateProviderId &&
                                 (value.Status == "Pending" || value.Status == "Running"), cancellationToken);
        var users = required.Select(value => value.Instrument.UserId).Distinct().ToList();
        var compared = 0;
        var differences = 0;
        var warnings = new List<string>();
        foreach (var userId in users)
        {
            context.SetCurrentUser(userId);
            var activePortfolio = await new InvestmentPortfolioService(
                    context, new InvestmentAccountingService(), active)
                .GetPortfolioAsync("all", cancellationToken);
            var candidatePortfolio = await new InvestmentPortfolioService(
                    context, new InvestmentAccountingService(), candidate)
                .GetPortfolioAsync("all", cancellationToken);
            if (activePortfolio.Summary.TotalValue is not { } activeValue ||
                candidatePortfolio.Summary.TotalValue is not { } candidateValue)
                continue;
            compared++;
            var denominator = Math.Max(Math.Abs(activeValue), 0.01m);
            var difference = Math.Abs(candidateValue - activeValue) / denominator * 100m;
            if (difference > _options.CutoverTolerancePercent)
            {
                differences++;
                warnings.Add($"User {userId} differs by {difference:F2}% between active and candidate market data.");
            }
        }
        var ready = candidate.Descriptor.IsConfigured && missingMappings == 0 && stalePrices == 0 &&
                    staleFx == 0 && incompleteJobs == 0 && (differences == 0 || approveDifferences);
        return new MarketDataCutoverReport(
            active.Descriptor.Id, candidateProviderId, required.Count, missingMappings, stalePrices,
            staleFx, incompleteJobs, compared, differences, _options.CutoverTolerancePercent,
            _options.RollbackRetentionDays, approveDifferences, ready, warnings);
    }

    private async Task<List<RequiredInstrument>> RequiredInstrumentsAsync(CancellationToken cancellationToken)
    {
        var transactions = await context.InvestmentTransactions.IgnoreQueryFilters().AsNoTracking()
            .ToListAsync(cancellationToken);
        var ids = transactions.Select(value => value.InstrumentId).Distinct().ToList();
        var instruments = await context.InvestmentInstruments.IgnoreQueryFilters().AsNoTracking()
            .Where(value => ids.Contains(value.Id) && !value.IsCustom && !value.IsArchived)
            .ToListAsync(cancellationToken);
        return instruments.Select(instrument => new RequiredInstrument(
            instrument,
            transactions.Where(value => value.InstrumentId == instrument.Id).Min(value => value.TradeDate)))
            .ToList();
    }

    private async Task<List<RequiredFxPair>> RequiredFxPairsAsync(CancellationToken cancellationToken)
    {
        var settings = await context.FinancialSettings.IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        var instruments = await RequiredInstrumentsAsync(cancellationToken);
        var cashFlows = await context.InvestmentCashFlows.IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        var pairs = new Dictionary<(string Base, string Quote), DateOnly>();
        foreach (var setting in settings)
        {
            var quote = setting.Currency.ToUpperInvariant();
            var userInstruments = instruments.Where(value => value.Instrument.UserId == setting.UserId).ToList();
            foreach (var item in userInstruments.Where(value => !value.Instrument.Currency.Equals(quote, StringComparison.OrdinalIgnoreCase)))
                AddPair(item.Instrument.Currency, quote, item.EarliestTradeDate);
            foreach (var flow in cashFlows.Where(value => value.UserId == setting.UserId && !value.Currency.Equals(quote, StringComparison.OrdinalIgnoreCase)))
                AddPair(flow.Currency, quote, flow.Date);
            foreach (var flow in cashFlows.Where(value => value.UserId == setting.UserId &&
                         value.ToCurrency is not null && !value.ToCurrency.Equals(quote, StringComparison.OrdinalIgnoreCase)))
                AddPair(flow.ToCurrency!, quote, flow.Date);
            if (quote != CurrencyCatalog.ReferenceCurrency && (userInstruments.Count > 0 || cashFlows.Any(value => value.UserId == setting.UserId)))
                AddPair(CurrencyCatalog.ReferenceCurrency, quote, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-7));
        }
        return pairs.Select(value => new RequiredFxPair(value.Key.Base, value.Key.Quote, value.Value)).ToList();

        void AddPair(string baseCurrency, string quoteCurrency, DateOnly earliest)
        {
            var key = (baseCurrency.ToUpperInvariant(), quoteCurrency.ToUpperInvariant());
            if (!pairs.TryGetValue(key, out var current) || earliest < current) pairs[key] = earliest;
        }
    }

    private async Task<bool> HasRequiredPriceCoverageAsync(
        string providerId, string externalId, DateOnly earliest, CancellationToken cancellationToken)
    {
        var rows = context.MarketPriceBars.AsNoTracking().Where(value =>
            value.Provider == providerId && value.ExternalInstrumentId == externalId);
        return await rows.AnyAsync(cancellationToken) &&
               await rows.MinAsync(value => value.MarketDate, cancellationToken) <= earliest &&
               await rows.MaxAsync(value => value.FetchedAt, cancellationToken) >= FreshnessCutoff();
    }

    private async Task<bool> HasRequiredFxCoverageAsync(
        string providerId, string baseCurrency, string quoteCurrency, DateOnly earliest,
        CancellationToken cancellationToken)
    {
        var rows = context.FxRateBars.AsNoTracking().Where(value => value.Provider == providerId &&
            value.BaseCurrency == baseCurrency && value.QuoteCurrency == quoteCurrency);
        return await rows.AnyAsync(cancellationToken) &&
               await rows.MinAsync(value => value.MarketDate, cancellationToken) <= earliest &&
               await rows.MaxAsync(value => value.FetchedAt, cancellationToken) >= FreshnessCutoff();
    }

    private DateTime FreshnessCutoff()
        => DateTime.UtcNow.AddMinutes(-Math.Max(1, _options.FreshnessMinutes));

    private async Task UpsertPricesAsync(string providerId, InvestmentInstrumentMarketMapping mapping,
        IReadOnlyList<ProviderPriceBar> bars, CancellationToken cancellationToken)
    {
        var existing = await context.MarketPriceBars.Where(value =>
            value.Provider == providerId && value.ExternalInstrumentId == mapping.ExternalInstrumentId)
            .ToDictionaryAsync(value => value.MarketDate, cancellationToken);
        foreach (var bar in bars)
        {
            if (existing.TryGetValue(bar.Date, out var row)) { row.Close = bar.Close; row.FetchedAt = DateTime.UtcNow; }
            else context.MarketPriceBars.Add(new MarketPriceBar
            {
                Provider = providerId, ExternalInstrumentId = mapping.ExternalInstrumentId,
                Symbol = mapping.DisplaySymbol ?? mapping.ExternalInstrumentId, Mic = mapping.DisplayMic ?? "",
                MarketDate = bar.Date, Close = bar.Close
            });
        }
    }

    private async Task UpsertFxAsync(string providerId, string baseCurrency, string quoteCurrency,
        IReadOnlyList<ProviderFxBar> bars, CancellationToken cancellationToken)
    {
        var existing = await context.FxRateBars.Where(value => value.Provider == providerId &&
                value.BaseCurrency == baseCurrency && value.QuoteCurrency == quoteCurrency)
            .ToDictionaryAsync(value => value.MarketDate, cancellationToken);
        foreach (var bar in bars)
        {
            if (existing.TryGetValue(bar.Date, out var row)) { row.Rate = bar.Rate; row.FetchedAt = DateTime.UtcNow; }
            else context.FxRateBars.Add(new FxRateBar
            {
                Provider = providerId, BaseCurrency = baseCurrency, QuoteCurrency = quoteCurrency,
                MarketDate = bar.Date, Rate = bar.Rate
            });
        }
    }

    private static bool MicMatches(InvestmentInstrument instrument, string? candidateMic)
    {
        var expected = instrument.ProviderMic ?? instrument.Mic;
        return string.IsNullOrWhiteSpace(expected) || string.Equals(expected, candidateMic, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RequiredInstrument(InvestmentInstrument Instrument, DateOnly EarliestTradeDate);
    private sealed record RequiredFxPair(string BaseCurrency, string QuoteCurrency, DateOnly EarliestDate);
}
