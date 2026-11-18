using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

internal static class MarketPriceHistory
{
    internal static async Task<List<MarketPriceBar>> ReadTrailingAsync(
        AppDbContext context,
        string providerId,
        string[] instrumentIds,
        DateOnly through,
        int countPerInstrument,
        CancellationToken cancellationToken)
    {
        if (instrumentIds.Length == 0) return [];
        if (context.Database.IsNpgsql())
        {
            // One index seek per instrument avoids walking years of retained prices for a latest close.
            return await context.MarketPriceBars.FromSql($"""
                SELECT bar.*
                FROM unnest(CAST({instrumentIds} AS text[])) AS instrument(id)
                CROSS JOIN LATERAL (
                    SELECT * FROM "MarketPriceBars" AS price
                    WHERE price."Provider" = {providerId}
                      AND price."ExternalInstrumentId" = instrument.id
                      AND price."MarketDate" <= {through}
                    ORDER BY price."MarketDate" DESC
                    LIMIT {countPerInstrument}
                ) AS bar
                """).AsNoTracking().ToListAsync(cancellationToken);
        }

        var result = new List<MarketPriceBar>();
        foreach (var id in instrumentIds)
        {
            result.AddRange(await context.MarketPriceBars.AsNoTracking()
                .Where(bar => bar.Provider == providerId && bar.ExternalInstrumentId == id && bar.MarketDate <= through)
                .OrderByDescending(bar => bar.MarketDate)
                .Take(countPerInstrument)
                .ToListAsync(cancellationToken));
        }
        return result;
    }
}
