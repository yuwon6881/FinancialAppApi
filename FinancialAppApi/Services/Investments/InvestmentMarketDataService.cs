using System.Data;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Services.Investments;

public sealed record InstrumentSearchDto(
    Guid? SelectedInstrumentId,
    string Symbol,
    string Name,
    string Type,
    string? Exchange,
    string? Mic,
    string? Country,
    string Currency,
    bool AvailableOnBasic,
    string Source);

public sealed record InvestmentSearchResponse(
    IReadOnlyList<InstrumentSearchDto> Results,
    bool ProviderConfigured,
    bool ProviderContacted,
    string? Message);

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
    ILogger<InvestmentMarketDataService> logger)
{
    private readonly MarketDataOptions _options = options.Value;

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

    public async Task<InvestmentSearchResponse> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeQuery(query);
        if (normalized.Length < 3)
        {
            return new InvestmentSearchResponse([], provider.IsConfigured, false, "Enter at least three characters.");
        }

        var local = await context.InvestmentInstruments.AsNoTracking()
            .Where(value => value.Symbol.ToUpper().Contains(normalized) ||
                            value.Name.ToUpper().Contains(normalized))
            .OrderBy(value => value.Symbol)
            .Take(20)
            .Select(value => new InstrumentSearchDto(
                value.Id, value.Symbol, value.Name, value.Type, value.Exchange, value.Mic,
                value.Country, value.Currency, true, value.IsCustom ? "manual" : "saved"))
            .ToListAsync(cancellationToken);

        var cached = await context.InstrumentSearchCaches
            .FirstOrDefaultAsync(value =>
                value.NormalizedQuery == normalized && value.ExpiresAt > DateTime.UtcNow,
                cancellationToken);
        if (cached is not null)
        {
            var cachedResults = JsonSerializer.Deserialize<List<InstrumentSearchResult>>(cached.ResultsJson) ?? [];
            return new InvestmentSearchResponse(
                Merge(local, cachedResults),
                provider.IsConfigured,
                false,
                null);
        }

        if (!provider.IsConfigured)
        {
            return new InvestmentSearchResponse(
                local,
                false,
                false,
                "Market search is not configured. You can add a custom instrument.");
        }

        if (!await ReserveDiscoveryQuotaAsync(cancellationToken))
        {
            return new InvestmentSearchResponse(
                local,
                true,
                false,
                "Market discovery is briefly busy. Please wait a moment and try again.");
        }

        try
        {
            var remote = await provider.SearchAsync(query.Trim(), cancellationToken);
            var stale = await context.InstrumentSearchCaches
                .FirstOrDefaultAsync(value => value.NormalizedQuery == normalized, cancellationToken);
            if (stale is null)
            {
                context.InstrumentSearchCaches.Add(new InstrumentSearchCache
                {
                    NormalizedQuery = normalized,
                    ResultsJson = JsonSerializer.Serialize(remote),
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                });
            }
            else
            {
                stale.ResultsJson = JsonSerializer.Serialize(remote);
                stale.ExpiresAt = DateTime.UtcNow.AddHours(24);
                stale.CreatedAt = DateTime.UtcNow;
            }
            await context.SaveChangesAsync(cancellationToken);
            return new InvestmentSearchResponse(Merge(local, remote), true, true, null);
        }
        catch (MarketDataProviderException exception)
        {
            logger.LogWarning("Investment symbol search failed with category {Failure}.", exception.Failure);
            return new InvestmentSearchResponse(local, true, true, exception.Message);
        }
    }

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
        if (!provider.IsConfigured)
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
        var instruments = heldInstruments
            .Where(value => !value.IsCustom && value.ProviderSymbol != null)
            .ToList();
        var hasInvestmentData = transactions.Count > 0 || cashFlows.Count > 0;
        var currencies = heldInstruments.Select(value => value.Currency)
            .Concat(cashFlows.Select(value => value.Currency))
            .Concat(appCurrency == "USD" || !hasInvestmentData ? [] : ["USD"])
            .Where(value => !value.Equals(appCurrency, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (instruments.Count == 0 && currencies.Count == 0)
            return new MarketRefreshResponse(null, "Complete", 0, 0, null, true, [], "Nothing to update.");

        var activeJob = await context.MarketDataRefreshJobs
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefaultAsync(value =>
                (value.Status == "Pending" || value.Status == "Running") &&
                value.ReportingCurrency == appCurrency, cancellationToken);
        var staleJobs = await context.MarketDataRefreshJobs
            .Where(value => (value.Status == "Pending" || value.Status == "Running") &&
                            value.ReportingCurrency != appCurrency)
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

        var allowance = await ReserveQuotaAsync(Math.Min(items.Count, Math.Max(1, _options.RefreshCallsPerMinute)), cancellationToken);
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
        foreach (var instrument in instruments.Where(value =>
                     !value.IsCustom && !string.IsNullOrWhiteSpace(value.ProviderSymbol)))
        {
            var fetchedAt = await context.MarketPriceBars.AsNoTracking()
                .Where(value => value.Provider == "twelvedata" &&
                                value.Symbol == instrument.ProviderSymbol &&
                                value.Mic == (instrument.ProviderMic ?? ""))
                .MaxAsync(value => (DateTime?)value.FetchedAt, cancellationToken);
            if (fetchedAt is null || fetchedAt < cutoff) return true;
        }

        var currencies = instruments.Select(value => value.Currency)
            .Concat(await context.InvestmentCashFlows.AsNoTracking()
                .Select(value => value.Currency).ToListAsync(cancellationToken))
            .Concat(appCurrency == "USD" || !hasInvestmentData ? [] : ["USD"])
            .Where(value => !value.Equals(appCurrency, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var currency in currencies)
        {
            var fetchedAt = await context.FxRateBars.AsNoTracking()
                .Where(value => value.Provider == "twelvedata" &&
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
        var latest = await context.MarketPriceBars
            .Where(value => value.Provider == "twelvedata" &&
                            value.Symbol == instrument.ProviderSymbol &&
                            value.Mic == (instrument.ProviderMic ?? ""))
            .OrderByDescending(value => value.MarketDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null &&
            DateTime.UtcNow - latest.FetchedAt < TimeSpan.FromMinutes(Math.Max(1, _options.FreshnessMinutes)))
        {
            return null;
        }
        var earliest = transactions.Where(value => value.InstrumentId == instrument.Id).Min(value => value.TradeDate);
        var start = latest is null ? earliest : latest.MarketDate.AddDays(-7);
        var bars = await provider.GetDailySeriesAsync(
            instrument.ProviderSymbol!,
            instrument.ProviderMic,
            start,
            cancellationToken);
        if (bars.Count == 0)
        {
            // Delisted symbol, market holiday, or a wrong ProviderSymbol mapping. Record the
            // attempt anyway; otherwise the freshness gate never advances and this symbol
            // re-burns a provider call on every refresh, forever.
            if (latest is not null) latest.FetchedAt = DateTime.UtcNow;
            logger.LogInformation(
                "Provider returned no price bars for {Symbol}.", instrument.ProviderSymbol);
            return $"No recent prices were available for {instrument.Symbol}. Check the symbol mapping if this persists.";
        }
        foreach (var bar in bars)
        {
            var existing = await context.MarketPriceBars.FirstOrDefaultAsync(value =>
                value.Provider == "twelvedata" &&
                value.Symbol == instrument.ProviderSymbol &&
                value.Mic == (instrument.ProviderMic ?? "") &&
                value.MarketDate == bar.Date, cancellationToken);
            if (existing is null)
            {
                context.MarketPriceBars.Add(new MarketPriceBar
                {
                    Symbol = instrument.ProviderSymbol!,
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
            .Where(value => value.Provider == "twelvedata" &&
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
                value.Provider == "twelvedata" &&
                value.BaseCurrency == baseCurrency &&
                value.QuoteCurrency == quoteCurrency &&
                value.MarketDate == bar.Date, cancellationToken);
            if (existing is null)
            {
                context.FxRateBars.Add(new FxRateBar
                {
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

    private async Task<int> ReserveQuotaAsync(int requested, CancellationToken cancellationToken)
    {
        if (requested <= 0) return 0;
        var result = 0;
        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var minuteStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            var dayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireQuotaLockAsync(cancellationToken);
            await PruneExpiredWindowsAsync(now, cancellationToken);
            var minute = await GetOrCreateWindowAsync("refresh-minute", minuteStart, cancellationToken);
            var providerMinute = await GetOrCreateWindowAsync("provider-minute", minuteStart, cancellationToken);
            var day = await GetOrCreateWindowAsync("provider-day", dayStart, cancellationToken);
            // The provider pool is shared by every tenant, so the right to spend it is metered
            // per user as well as globally. Price data itself stays global and shared.
            var userDay = await GetOrCreateWindowAsync(UserDayScope(), dayStart, cancellationToken);
            var allowed = Math.Min(requested, Math.Min(
                Math.Max(0, _options.RefreshCallsPerMinute - minute.Used),
                Math.Min(
                    Math.Max(0, _options.RefreshCallsPerMinute + 2 - providerMinute.Used),
                    Math.Min(
                        Math.Max(0, _options.DailyCallCeiling - day.Used),
                        Math.Max(0, _options.PerUserDailyCallCeiling - userDay.Used)))));
            if (allowed > 0)
            {
                minute.Used += allowed;
                minute.UpdatedAt = now;
                providerMinute.Used += allowed;
                providerMinute.UpdatedAt = now;
                day.Used += allowed;
                day.UpdatedAt = now;
                userDay.Used += allowed;
                userDay.UpdatedAt = now;
                await context.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            result = allowed;
        });
        return result;
    }

    private async Task<bool> ReserveDiscoveryQuotaAsync(CancellationToken cancellationToken)
    {
        var result = false;
        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var minuteStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            var dayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireQuotaLockAsync(cancellationToken);
            await PruneExpiredWindowsAsync(now, cancellationToken);
            var providerMinute = await GetOrCreateWindowAsync("provider-minute", minuteStart, cancellationToken);
            var discoveryMinute = await GetOrCreateWindowAsync("discovery-minute", minuteStart, cancellationToken);
            var day = await GetOrCreateWindowAsync("provider-day", dayStart, cancellationToken);
            var userDay = await GetOrCreateWindowAsync(UserDayScope(), dayStart, cancellationToken);
            var allowed = discoveryMinute.Used < 2 &&
                          providerMinute.Used < _options.RefreshCallsPerMinute + 2 &&
                          day.Used < _options.DailyCallCeiling &&
                          userDay.Used < _options.PerUserDailyCallCeiling;
            if (allowed)
            {
                discoveryMinute.Used++;
                discoveryMinute.UpdatedAt = now;
                providerMinute.Used++;
                providerMinute.UpdatedAt = now;
                day.Used++;
                day.UpdatedAt = now;
                userDay.Used++;
                userDay.UpdatedAt = now;
                await context.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            result = allowed;
        });
        return result;
    }

    private Task AcquireQuotaLockAsync(CancellationToken cancellationToken)
        => context.Database.IsRelational()
            ? context.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(741923601)",
                cancellationToken)
            : Task.CompletedTask;

    private string UserDayScope() => $"user-day:{context.RequireCurrentUserId()}";

    /// <summary>
    /// Quota windows are write-once-per-minute-per-scope and never read again after their
    /// window closes, so they are pruned lazily on the same path that reserves quota -- the
    /// pattern already used for expired sessions and WebAuthn challenges.
    /// </summary>
    private async Task PruneExpiredWindowsAsync(DateTime now, CancellationToken cancellationToken)
    {
        // One day is the longest window in use; anything older can never be reserved against.
        var cutoff = now.AddDays(-1).AddHours(-1);
        var expired = await context.MarketDataQuotaWindows
            .Where(value => value.WindowStart < cutoff)
            .Take(500)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0) return;
        context.MarketDataQuotaWindows.RemoveRange(expired);
    }

    private async Task<MarketDataQuotaWindow> GetOrCreateWindowAsync(
        string scope,
        DateTime start,
        CancellationToken cancellationToken)
    {
        var window = await context.MarketDataQuotaWindows
            .FirstOrDefaultAsync(value => value.Scope == scope && value.WindowStart == start, cancellationToken);
        if (window is not null) return window;
        window = new MarketDataQuotaWindow { Scope = scope, WindowStart = start };
        context.MarketDataQuotaWindows.Add(window);
        return window;
    }

    private static List<InstrumentSearchDto> Merge(
        IReadOnlyList<InstrumentSearchDto> local,
        IReadOnlyList<InstrumentSearchResult> remote)
    {
        var results = local.ToList();
        foreach (var value in remote)
        {
            if (results.Any(existing =>
                    existing.Symbol.Equals(value.Symbol, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Mic, value.Mic, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            results.Add(new InstrumentSearchDto(
                null, value.Symbol, value.Name, value.Type, value.Exchange, value.Mic,
                value.Country, value.Currency, value.AvailableOnBasic, value.Provider));
        }
        return results.Take(30).ToList();
    }

    private static string NormalizeQuery(string value)
    {
        var normalized = string.Join(
            ' ',
            value.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : normalized[..120];
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
}
