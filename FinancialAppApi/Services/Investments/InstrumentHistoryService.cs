using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

public sealed record InstrumentHistoryPointDto(DateOnly Date, decimal Price);

public sealed record InstrumentHistoryDto(
    Guid InstrumentId,
    string Symbol,
    string Name,
    string Currency,
    IReadOnlyList<InstrumentHistoryPointDto> Points,
    decimal? AverageCostNative,
    decimal? LatestPriceNative,
    decimal Units,
    DateOnly? FirstBoughtOn);

/// <summary>
/// The price history of a single fund, for the detail view behind a holding row.
/// Reads only stored price bars — it never calls the market-data provider, whose
/// quota is shared across every user.
/// </summary>
public sealed class InstrumentHistoryService(
    AppDbContext context,
    InvestmentAccountingService accounting)
{
    public async Task<InstrumentHistoryDto?> GetAsync(
        Guid instrumentId,
        string range,
        CancellationToken cancellationToken)
    {
        var instrument = await context.InvestmentInstruments.AsNoTracking()
            .FirstOrDefaultAsync(value => value.Id == instrumentId, cancellationToken);
        if (instrument is null) return null;

        var transactions = await context.InvestmentTransactions.AsNoTracking()
            .Include(value => value.Instrument)
            .Where(value => value.InstrumentId == instrumentId)
            .OrderBy(value => value.TradeDate)
            .ToListAsync(cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = InvestmentChartRange.StartFor(range, today);
        var points = await LoadPointsAsync(instrument, from, today, cancellationToken);

        // Positions are per (account, instrument); a fund held in two brokers is one
        // fund to the reader, so combine them into a single average price paid.
        var positions = accounting
            .Calculate(transactions, instrument.Currency, _ => 1m)
            .Positions
            .Where(value => value.InstrumentId == instrumentId)
            .ToList();
        var units = positions.Sum(value => value.Units);
        var costBasis = positions.Sum(value => value.CostBasisNative);

        return new InstrumentHistoryDto(
            instrument.Id,
            instrument.Symbol,
            instrument.Name,
            instrument.Currency,
            points,
            units > 0 ? costBasis / units : null,
            points.Count > 0 ? points[^1].Price : null,
            units,
            transactions.FirstOrDefault(value => value.Type == "Buy")?.TradeDate);
    }

    private async Task<IReadOnlyList<InstrumentHistoryPointDto>> LoadPointsAsync(
        InvestmentInstrument instrument,
        DateOnly? from,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        if (instrument.IsCustom || string.IsNullOrWhiteSpace(instrument.ProviderSymbol)) return [];

        var symbol = instrument.ProviderSymbol!;
        var mic = instrument.ProviderMic ?? "";
        var bars = await context.MarketPriceBars.AsNoTracking()
            .Where(value => value.Provider == "twelvedata" &&
                            value.Symbol == symbol &&
                            value.MarketDate <= today &&
                            (from == null || value.MarketDate >= from))
            .ToListAsync(cancellationToken);

        // Symbol and MIC are compared case-insensitively, as everywhere else that
        // resolves prices: a single differently-cased row would otherwise drop out
        // and leave a gap in the line.
        // A symbol can also hold several rows for one date after a re-fetch; the
        // most recently fetched row wins, matching how holdings are valued.
        var points = bars
            .Where(value => value.Mic.Equals(mic, StringComparison.OrdinalIgnoreCase))
            .GroupBy(value => value.MarketDate)
            .Select(group => group.OrderByDescending(value => value.FetchedAt).First())
            .OrderBy(value => value.MarketDate)
            .Select(value => new InstrumentHistoryPointDto(value.MarketDate, value.Close))
            .ToList();
        return InvestmentChartRange.Sample(points);
    }
}
