using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

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
    string Source,
    string Availability,
    string? AvailabilityMessage,
    MarketInstrumentReference? MarketDataReference);

public sealed record InvestmentSearchResponse(
    IReadOnlyList<InstrumentSearchDto> Results,
    bool ProviderConfigured,
    bool ProviderContacted,
    string? Message);

public sealed class InvestmentMarketSearchService(
    AppDbContext context,
    IMarketDataProvider provider,
    MarketDataQuotaService quota,
    ILogger<InvestmentMarketSearchService> logger)
{
    private string ProviderId => provider.Descriptor.Id;

    public async Task<InvestmentSearchResponse> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var normalized = NormalizeQuery(query);
        if (normalized.Length < 3)
            return new InvestmentSearchResponse([], provider.Descriptor.IsConfigured, false, "Enter at least three characters.");

        var local = await context.InvestmentInstruments.AsNoTracking()
            .Where(value => value.Symbol.ToUpper().Contains(normalized) || value.Name.ToUpper().Contains(normalized))
            .OrderBy(value => value.Symbol).Take(20)
            .Select(value => new InstrumentSearchDto(
                value.Id, value.Symbol, value.Name, value.Type, value.Exchange, value.Mic,
                value.Country, value.Currency, true, value.IsCustom ? "manual" : "saved",
                MarketInstrumentAvailability.Available.ToString(), null, null))
            .ToListAsync(cancellationToken);

        var cached = await context.InstrumentSearchCaches.FirstOrDefaultAsync(value =>
            value.ProviderId == ProviderId && value.NormalizedQuery == normalized && value.ExpiresAt > DateTime.UtcNow,
            cancellationToken);
        if (cached is not null)
        {
            var cachedResults = JsonSerializer.Deserialize<List<InstrumentSearchResult>>(cached.ResultsJson) ?? [];
            return new InvestmentSearchResponse(Merge(local, cachedResults), provider.Descriptor.IsConfigured, false, null);
        }
        if (!provider.Descriptor.IsConfigured)
            return new InvestmentSearchResponse(local, false, false,
                "Market search is not configured. You can add a custom instrument.");
        if (!await quota.ReserveDiscoveryAsync(cancellationToken))
            return new InvestmentSearchResponse(local, true, false,
                "Market discovery is briefly busy. Please wait a moment and try again.");

        try
        {
            var remote = await provider.SearchAsync(query.Trim(), cancellationToken);
            var stale = await context.InstrumentSearchCaches.FirstOrDefaultAsync(value =>
                value.ProviderId == ProviderId && value.NormalizedQuery == normalized, cancellationToken);
            if (stale is null)
            {
                context.InstrumentSearchCaches.Add(new InstrumentSearchCache
                {
                    ProviderId = ProviderId, NormalizedQuery = normalized,
                    ResultsJson = JsonSerializer.Serialize(remote), ExpiresAt = DateTime.UtcNow.AddHours(24)
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

    private static List<InstrumentSearchDto> Merge(
        IReadOnlyList<InstrumentSearchDto> local,
        IReadOnlyList<InstrumentSearchResult> remote)
    {
        var results = local.ToList();
        foreach (var value in remote)
        {
            if (results.Any(existing => existing.Symbol.Equals(value.Symbol, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(existing.Mic, value.Mic, StringComparison.OrdinalIgnoreCase)))
                continue;
            results.Add(new InstrumentSearchDto(
                null, value.Symbol, value.Name, value.Type, value.Exchange, value.Mic,
                value.Country, value.Currency, value.Availability != MarketInstrumentAvailability.Unavailable,
                value.MarketDataReference.ProviderId, value.Availability.ToString(),
                value.AvailabilityMessage, value.MarketDataReference));
        }
        return results.Take(30).ToList();
    }

    private static string NormalizeQuery(string value)
    {
        var normalized = string.Join(' ', value.Trim().ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }
}
