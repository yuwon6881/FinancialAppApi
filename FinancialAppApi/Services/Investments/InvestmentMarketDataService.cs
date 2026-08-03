using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Services.Investments;

public sealed record MarketRefreshResponse(
    Guid? JobId,
    string Status,
    int Updated,
    int Total,
    int? RetryAfterSeconds,
    bool Complete,
    IReadOnlyList<string> Warnings,
    string? Message);

public sealed class InvestmentMarketDataService(
    AppDbContext context,
    IMarketDataProvider provider,
    IOptions<MarketDataOptions> options,
    ILogger<InvestmentMarketDataService> logger,
    MarketDataQuotaService? quotaService = null,
    InvestmentMarketSearchService? searchService = null)
{
    private readonly MarketDataOptions _options = options.Value;
    private readonly MarketDataQuotaService _quota = quotaService ?? new MarketDataQuotaService(context, provider);
    private readonly InvestmentMarketSearchService _search = searchService ?? new InvestmentMarketSearchService(
        context, provider, quotaService ?? new MarketDataQuotaService(context, provider),
        new ForwardingSearchLogger(logger));
    private string ProviderId => provider.Descriptor.Id;
    private MarketDataQuotaPolicy QuotaPolicy => provider.Descriptor.QuotaPolicy;

    /// <summary>How many times a single pending item is retried before it is abandoned.</summary>
    private const int MaxItemAttempts = 3;
    private const string AttemptsMarker = "|attempts=";

    private static (string Key, int Attempts) ParseItem(string item)
    {
        var separator = item.LastIndexOf(AttemptsMarker, StringComparison.Ordinal);
        if (separator < 0) return (item, 0);
        var parsed = int.TryParse(item[(separator + AttemptsMarker.Length)..], out var attempts) ? attempts : 0;
        return (item[..separator], parsed);
    }

    private static string WithAttempts(string key, int attempts)
        => attempts <= 0 ? key : $"{key}{AttemptsMarker}{attempts}";

    public Task<InvestmentSearchResponse> SearchAsync(string query, CancellationToken cancellationToken)
        => _search.SearchAsync(query, cancellationToken);

    /// <summary>
    /// Refreshes market data for the current user, subject to the freshness gate. The gate is
    /// deliberately not caller-controllable: it is the only thing stopping one tenant from
    /// looping this call and draining the shared provider ceiling.
    /// </summary>
    public Task<MarketRefreshResponse> RefreshAsync(CancellationToken cancellationToken)
        => RefreshAsync(true, cancellationToken);

    // respectFreshnessGate: only an internal or scheduled caller may pass false, and none does today.
    private async Task<MarketRefreshResponse> RefreshAsync(
        bool respectFreshnessGate,
        CancellationToken cancellationToken)
    {
        if (!provider.Descriptor.IsConfigured)
        {
            return new MarketRefreshResponse(
                null, "ConfigurationRequired", 0, 0, null, true, [],
                "Market refresh is not configured. Manual prices remain available.");
        }

        if (respectFreshnessGate && !await AutomaticRefreshRequiredAsync(cancellationToken))
        {
            return new MarketRefreshResponse(
                null, "Fresh", 0, 0, null, true, [], "Market data is less than one hour old.");
        }

        var appCurrency = (await context.FinancialSettings.AsNoTracking()
                .Select(value => value.Currency)
                .FirstOrDefaultAsync(cancellationToken) ?? "USD")
            .ToUpperInvariant();
        var transactions = await context.InvestmentTransactions.AsNoTracking()
            .ToListAsync(cancellationToken);
        var cashFlows = await context.InvestmentCashFlows.AsNoTracking().ToListAsync(cancellationToken);

        var instrumentIds = transactions.Select(value => value.InstrumentId).Distinct().ToList();
        var heldInstruments = await context.InvestmentInstruments
            .Where(value => instrumentIds.Contains(value.Id) && !value.IsArchived)
            .ToListAsync(cancellationToken);
        var instruments = new List<InvestmentInstrument>();
        foreach (var instrument in heldInstruments.Where(value => !value.IsCustom))
        {
            if (await MarketDataReferenceResolver.ResolveAsync(context, instrument, provider, cancellationToken) is not null)
                instruments.Add(instrument);
        }
        var hasInvestmentData = transactions.Count > 0 || cashFlows.Count > 0;
        var currencies = heldInstruments.Select(value => value.Currency)
            .Concat(cashFlows.Select(value => value.Currency))
            .Concat(cashFlows.Where(value => value.ToCurrency is not null).Select(value => value.ToCurrency!))
            .Concat(appCurrency == "USD" || !hasInvestmentData ? [] : ["USD"])
            .Where(value => !value.Equals(appCurrency, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (instruments.Count == 0 && currencies.Count == 0)
            return new MarketRefreshResponse(null, "Complete", 0, 0, null, true, [], "Nothing to update.");

        var activeJob = await context.MarketDataRefreshJobs
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefaultAsync(value =>
                value.ProviderId == ProviderId &&
                (value.Status == "Pending" || value.Status == "Running") &&
                value.ReportingCurrency == appCurrency, cancellationToken);
        var staleJobs = await context.MarketDataRefreshJobs
            .Where(value => (value.Status == "Pending" || value.Status == "Running") &&
                            (value.ProviderId != ProviderId || value.ReportingCurrency != appCurrency))
            .ToListAsync(cancellationToken);
        foreach (var stale in staleJobs)
        {
            stale.Status = "Superseded";
            stale.CompletedAt = DateTime.UtcNow;
            stale.UpdatedAt = DateTime.UtcNow;
        }
        if (activeJob is null)
        {
            var pending = instruments.Select(value => $"instrument:{value.Id}").ToList();
            pending.AddRange(currencies
                .Select(value => $"fx:{value}:{appCurrency}")
                .Distinct(StringComparer.OrdinalIgnoreCase));
            activeJob = new MarketDataRefreshJob
            {
                Status = "Pending",
                ProviderId = ProviderId,
                ReportingCurrency = appCurrency,
                TotalItems = pending.Count,
                PendingItemsJson = JsonSerializer.Serialize(pending)
            };
            context.MarketDataRefreshJobs.Add(activeJob);
            await context.SaveChangesAsync(cancellationToken);
        }

        var items = JsonSerializer.Deserialize<List<string>>(activeJob.PendingItemsJson) ?? [];
        if (items.Count == 0)
        {
            activeJob.Status = "Complete";
            activeJob.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return ToResponse(activeJob, [], null);
        }

        var allowance = await _quota.ReserveRefreshAsync(Math.Min(items.Count, Math.Max(1, QuotaPolicy.RefreshCallsPerMinute)), cancellationToken);
        if (allowance == 0)
        {
            return ToResponse(activeJob, WarningList(activeJob.Warning), SecondsUntilNextMinute());
        }

        activeJob.Status = "Running";
        var warnings = WarningList(activeJob.Warning);
        foreach (var item in items.Take(allowance).ToList())
        {
            var (key, attempts) = ParseItem(item);
            var succeeded = true;
            try
            {
                if (key.StartsWith("instrument:", StringComparison.Ordinal))
                {
                    // A job outlives the rows it references: an instrument can be archived,
                    // deleted, or converted to custom between creation and resumption. Treat
                    // an unresolvable item as dropped -- throwing here would wedge the job
                    // permanently, since the item would never leave the pending list.
                    if (!Guid.TryParse(key["instrument:".Length..], out var id) ||
                        instruments.FirstOrDefault(value => value.Id == id) is not { } instrument)
                    {
                        logger.LogInformation("Dropping stale market refresh item {Item}.", key);
                        items.Remove(item);
                        continue;
                    }
                    var warning = await RefreshInstrumentAsync(instrument, transactions, cancellationToken);
                    if (warning is not null) warnings.Add(warning);
                }
                else
                {
                    var pair = key.Split(':');
                    if (pair.Length != 3 || !key.StartsWith("fx:", StringComparison.Ordinal))
                    {
                        logger.LogInformation("Dropping malformed market refresh item {Item}.", key);
                        items.Remove(item);
                        continue;
                    }
                    var relevantDates = transactions.Where(value =>
                            heldInstruments.Any(instrument => instrument.Id == value.InstrumentId &&
                                                          instrument.Currency.Equals(pair[1], StringComparison.OrdinalIgnoreCase)))
                        .Select(value => value.TradeDate)
                        .Concat(cashFlows
                            .Where(value => value.Currency.Equals(pair[1], StringComparison.OrdinalIgnoreCase))
                            .Select(value => value.Date))
                        .ToList();
                    var warning = await RefreshFxAsync(pair[1], pair[2],
                        relevantDates.Count == 0 ? DateOnly.FromDateTime(DateTime.UtcNow) : relevantDates.Min(),
                        cancellationToken);
                    if (warning is not null) warnings.Add(warning);
                }
            }
            catch (MarketDataProviderException exception)
            {
                succeeded = false;
                logger.LogWarning("Market refresh item failed with category {Failure}.", exception.Failure);
                warnings.Add(exception.Message);
            }
            items.Remove(item);
            if (succeeded)
            {
                activeJob.UpdatedItems++;
            }
            else if (attempts + 1 < MaxItemAttempts)
            {
                // A transient provider error must not silently drop the symbol and let the
                // job report Complete with fewer updates than it promised. Requeue it with
                // an attempt counter so it is retried a bounded number of times.
                items.Add(WithAttempts(key, attempts + 1));
            }
            else
            {
                logger.LogWarning("Giving up on market refresh item {Item} after {Attempts} attempts.", key, MaxItemAttempts);
                warnings.Add($"Could not refresh {key} after {MaxItemAttempts} attempts.");
            }
        }

        activeJob.PendingItemsJson = JsonSerializer.Serialize(items);
        activeJob.Warning = string.Join(" ", warnings.Distinct());
        activeJob.UpdatedAt = DateTime.UtcNow;
        if (items.Count == 0)
        {
            activeJob.Status = "Complete";
            activeJob.CompletedAt = DateTime.UtcNow;
        }
        else
        {
            activeJob.Status = "Pending";
        }
        await context.SaveChangesAsync(cancellationToken);
        return ToResponse(activeJob, warnings, items.Count > 0 ? SecondsUntilNextMinute() : null);
    }

    internal async Task<bool> AutomaticRefreshRequiredAsync(CancellationToken cancellationToken)
    {
        var appCurrency = (await context.FinancialSettings.AsNoTracking()
                .Select(value => value.Currency)
                .FirstOrDefaultAsync(cancellationToken) ?? "USD")
            .ToUpperInvariant();
        var instrumentIds = await context.InvestmentTransactions.AsNoTracking()
            .Select(value => value.InstrumentId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var instruments = await context.InvestmentInstruments.AsNoTracking()
            .Where(value => instrumentIds.Contains(value.Id) && !value.IsArchived)
            .ToListAsync(cancellationToken);
        var hasCashFlows = await context.InvestmentCashFlows.AsNoTracking()
            .AnyAsync(cancellationToken);
        var hasInvestmentData = instrumentIds.Count > 0 || hasCashFlows;
        var cutoff = DateTime.UtcNow.AddMinutes(-InvestmentAllocationService.AutomaticRefreshMinutes);
        foreach (var instrument in instruments.Where(value => !value.IsCustom))
        {
            var reference = await MarketDataReferenceResolver.ResolveAsync(
                context, instrument, provider, cancellationToken);
            if (reference is null) continue;
            var fetchedAt = await context.MarketPriceBars.AsNoTracking()
                .Where(value => value.Provider == ProviderId &&
                                value.ExternalInstrumentId == reference.ExternalId)
                .MaxAsync(value => (DateTime?)value.FetchedAt, cancellationToken);
            if (fetchedAt is null || fetchedAt < cutoff) return true;
        }

        var currencies = instruments.Select(value => value.Currency)
            .Concat(await context.InvestmentCashFlows.AsNoTracking()
                .Select(value => value.Currency).ToListAsync(cancellationToken))
            .Concat(await context.InvestmentCashFlows.AsNoTracking()
                .Where(value => value.ToCurrency != null)
                .Select(value => value.ToCurrency!).ToListAsync(cancellationToken))
            .Concat(appCurrency == "USD" || !hasInvestmentData ? [] : ["USD"])
            .Where(value => !value.Equals(appCurrency, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var currency in currencies)
        {
            var fetchedAt = await context.FxRateBars.AsNoTracking()
                .Where(value => value.Provider == ProviderId &&
                                value.BaseCurrency == currency &&
                                value.QuoteCurrency == appCurrency)
                .MaxAsync(value => (DateTime?)value.FetchedAt, cancellationToken);
            if (fetchedAt is null || fetchedAt < cutoff) return true;
        }
        return false;
    }

    /// <returns>A user-facing warning when the provider returned nothing, otherwise null.</returns>
    private async Task<string?> RefreshInstrumentAsync(
        InvestmentInstrument instrument,
        IReadOnlyList<InvestmentTransaction> transactions,
        CancellationToken cancellationToken)
    {
        var reference = await MarketDataReferenceResolver.ResolveAsync(
            context, instrument, provider, cancellationToken);
        if (reference is null)
            return $"No market-data mapping is available for {instrument.Symbol}.";
        var latest = await context.MarketPriceBars
            .Where(value => value.Provider == ProviderId &&
                            value.ExternalInstrumentId == reference.ExternalId)
            .OrderByDescending(value => value.MarketDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null &&
            DateTime.UtcNow - latest.FetchedAt < TimeSpan.FromMinutes(Math.Max(1, _options.FreshnessMinutes)))
        {
            return null;
        }
        var earliest = transactions.Where(value => value.InstrumentId == instrument.Id).Min(value => value.TradeDate);
        var start = latest is null
            ? earliest
            : latest.MarketDate.AddDays(-7);
        var bars = await provider.GetDailySeriesAsync(
            reference,
            start,
            cancellationToken);
        if (bars.Count == 0)
        {
            // Delisted symbol, market holiday, or a wrong ProviderSymbol mapping. Record the
            // attempt anyway; otherwise the freshness gate never advances and this symbol
            // re-burns a provider call on every refresh, forever.
            if (latest is not null) latest.FetchedAt = DateTime.UtcNow;
            logger.LogInformation(
                "Provider returned no price bars for {Symbol}.", instrument.Symbol);
            return $"No recent prices were available for {instrument.Symbol}. Check the symbol mapping if this persists.";
        }
        foreach (var bar in bars)
        {
            var existing = await context.MarketPriceBars.FirstOrDefaultAsync(value =>
                value.Provider == ProviderId &&
                value.ExternalInstrumentId == reference.ExternalId &&
                value.MarketDate == bar.Date, cancellationToken);
            if (existing is null)
            {
                context.MarketPriceBars.Add(new MarketPriceBar
                {
                    Provider = ProviderId,
                    ExternalInstrumentId = reference.ExternalId,
                    Symbol = instrument.ProviderSymbol ?? instrument.Symbol,
                    Mic = instrument.ProviderMic ?? "",
                    MarketDate = bar.Date,
                    Close = bar.Close
                });
            }
            else
            {
                existing.Close = bar.Close;
                existing.FetchedAt = DateTime.UtcNow;
            }
        }
        return null;
    }

    /// <returns>A user-facing warning when the provider returned nothing, otherwise null.</returns>
    private async Task<string?> RefreshFxAsync(
        string baseCurrency,
        string quoteCurrency,
        DateOnly earliest,
        CancellationToken cancellationToken)
    {
        var latest = await context.FxRateBars
            .Where(value => value.Provider == ProviderId &&
                            value.BaseCurrency == baseCurrency &&
                            value.QuoteCurrency == quoteCurrency)
            .OrderByDescending(value => value.MarketDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null &&
            DateTime.UtcNow - latest.FetchedAt < TimeSpan.FromMinutes(Math.Max(1, _options.FreshnessMinutes)))
        {
            return null;
        }
        var bars = await provider.GetFxSeriesAsync(
            baseCurrency,
            quoteCurrency,
            latest is null ? earliest : latest.MarketDate.AddDays(-7),
            cancellationToken);
        if (bars.Count == 0)
        {
            // See RefreshInstrumentAsync: record the attempt so the freshness gate advances
            // even when the provider has nothing for this pair.
            if (latest is not null) latest.FetchedAt = DateTime.UtcNow;
            logger.LogInformation(
                "Provider returned no FX bars for {Base}/{Quote}.", baseCurrency, quoteCurrency);
            return $"No recent {baseCurrency}/{quoteCurrency} exchange rates were available.";
        }
        foreach (var bar in bars)
        {
            var existing = await context.FxRateBars.FirstOrDefaultAsync(value =>
                value.Provider == ProviderId &&
                value.BaseCurrency == baseCurrency &&
                value.QuoteCurrency == quoteCurrency &&
                value.MarketDate == bar.Date, cancellationToken);
            if (existing is null)
            {
                context.FxRateBars.Add(new FxRateBar
                {
                    Provider = ProviderId,
                    BaseCurrency = baseCurrency,
                    QuoteCurrency = quoteCurrency,
                    MarketDate = bar.Date,
                    Rate = bar.Rate
                });
            }
            else
            {
                existing.Rate = bar.Rate;
                existing.FetchedAt = DateTime.UtcNow;
            }
        }
        return null;
    }

    private static int SecondsUntilNextMinute()
        => Math.Max(1, 60 - DateTime.UtcNow.Second);

    private static List<string> WarningList(string? warning)
        => string.IsNullOrWhiteSpace(warning) ? [] : [warning];

    private static MarketRefreshResponse ToResponse(
        MarketDataRefreshJob job,
        IReadOnlyList<string> warnings,
        int? retryAfter) => new(
        job.Id,
        job.Status,
        job.UpdatedItems,
        job.TotalItems,
        retryAfter,
        job.Status == "Complete",
        warnings.Distinct().ToList(),
        null);

    private sealed class ForwardingSearchLogger(ILogger<InvestmentMarketDataService> logger)
        : ILogger<InvestmentMarketSearchService>
    {
        IDisposable? ILogger.BeginScope<TState>(TState state) => logger.BeginScope(state);
        bool ILogger.IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);
        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => logger.Log(logLevel, eventId, state, exception, formatter);
    }
}
