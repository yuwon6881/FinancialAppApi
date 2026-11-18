using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

internal static class MarketPriceQueryCheck
{
    internal static async Task VerifyAsync(AppDbContext context)
    {
        const string provider = "market-price-query-check";
        var start = new DateOnly(2026, 1, 1);
        foreach (var id in new[] { "A", "B", "other" })
        {
            context.MarketPriceBars.AddRange(Enumerable.Range(0, 5).Select(index => new MarketPriceBar
            {
                Provider = provider,
                ExternalInstrumentId = id,
                Symbol = id,
                Mic = string.Empty,
                MarketDate = start.AddDays(index),
                FetchedAt = DateTime.UtcNow,
                Close = 100 + index,
            }));
        }
        await context.SaveChangesAsync();

        var bars = await MarketPriceHistory.ReadTrailingAsync(
            context, provider, ["A", "B"], start.AddDays(2), 2, CancellationToken.None);
        var expected = new[] { ("A", 101m), ("A", 102m), ("B", 101m), ("B", 102m) };
        var actual = bars.OrderBy(bar => bar.ExternalInstrumentId).ThenBy(bar => bar.MarketDate)
            .Select(bar => (bar.ExternalInstrumentId, bar.Close));
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException("The market price query returned the wrong trailing bars.");
    }
}
