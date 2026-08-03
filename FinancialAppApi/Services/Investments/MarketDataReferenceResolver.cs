using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

public static class MarketDataReferenceResolver
{
    public static async Task<MarketInstrumentReference?> ResolveAsync(
        AppDbContext context,
        InvestmentInstrument instrument,
        IMarketDataProvider provider,
        CancellationToken cancellationToken)
    {
        var providerId = provider.Descriptor.Id;
        var mapping = await context.InvestmentInstrumentMarketMappings.AsNoTracking()
            .FirstOrDefaultAsync(value =>
                value.InvestmentInstrumentId == instrument.Id && value.ProviderId == providerId,
                cancellationToken);
        if (mapping is not null)
            return new MarketInstrumentReference(mapping.ProviderId, mapping.ExternalInstrumentId);

        // Compatibility for databases and offline-created payloads from before provider mappings.
        // Each adapter owns the interpretation of its own legacy identifier fields.
        return provider.TryResolveLegacyReference(instrument.ProviderSymbol, instrument.ProviderMic);
    }
}
