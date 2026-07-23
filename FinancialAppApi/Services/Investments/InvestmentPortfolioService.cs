using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

public sealed record InvestmentSummaryDto(
    decimal GrowthLedgerBalance,
    decimal? MarketValue,
    decimal? CostBasis,
    decimal? UnrealisedProfitLoss,
    decimal? UnrealisedPercent,
    decimal? RealisedProfitLoss,
    decimal? NetDividends,
    decimal? DailyChange);

public sealed record InvestmentHoldingDto(
    Guid AccountId,
    string AccountName,
    Guid InstrumentId,
    string Symbol,
    string Name,
    string Type,
    string Currency,
    decimal Units,
    decimal AverageCostNative,
    decimal? LatestPriceNative,
    decimal? ValueNative,
    decimal? ValueApp,
    decimal? DailyChangeApp,
    decimal? UnrealisedProfitLossApp,
    decimal? UnrealisedPercent,
    DateOnly? PriceDate,
    DateTime? PriceFetchedAt,
    bool UsesManualPrice,
    bool FxIncomplete);

public sealed record InvestmentChartPointDto(
    DateOnly Date,
    decimal? MarketValue,
    decimal? CostBasis,
    decimal? NetContributions);

public sealed record InvestmentPortfolioDto(
    string AppCurrency,
    InvestmentSummaryDto Summary,
    IReadOnlyList<InvestmentAccount> Accounts,
    IReadOnlyList<InvestmentInstrument> Instruments,
    IReadOnlyList<InvestmentHoldingDto> Holdings,
    IReadOnlyList<InvestmentTransactionDto> Activity,
    IReadOnlyList<ManualPriceDto> ManualPrices,
    IReadOnlyList<InvestmentChartPointDto> Chart,
    IReadOnlyList<string> Insights,
    IReadOnlyList<string> Warnings,
    DateTime? PricesUpdatedAt,
    bool MarketDataConfigured);

public sealed record InvestmentTransactionDto(
    Guid Id,
    Guid AccountId,
    Guid InstrumentId,
    string Type,
    DateOnly TradeDate,
    decimal Units,
    decimal? UnitPrice,
    decimal? CashAmount,
    decimal Fees,
    decimal Taxes,
    decimal? TradeFxRate,
    string? Notes,
    Guid? LinkedTransferId,
    DateTime CreatedAt);

public sealed record ManualPriceDto(
    Guid Id,
    Guid InstrumentId,
    DateOnly MarketDate,
    decimal Price,
    decimal? FxRate);

public sealed class InvestmentPortfolioService(
    AppDbContext context,
    InvestmentAccountingService accounting,
    IMarketDataProvider provider)
{
    public async Task<InvestmentPortfolioDto> GetPortfolioAsync(
        string range,
        CancellationToken cancellationToken)
    {
        var appCurrency = (await context.FinancialSettings.AsNoTracking()
                .Select(value => value.Currency)
                .FirstOrDefaultAsync(cancellationToken) ?? "USD")
            .ToUpperInvariant();
        var accounts = await context.InvestmentAccounts.AsNoTracking()
            .OrderBy(value => value.IsArchived).ThenBy(value => value.Name)
            .ToListAsync(cancellationToken);
        var instruments = await context.InvestmentInstruments.AsNoTracking()
            .OrderBy(value => value.Symbol)
            .ToListAsync(cancellationToken);
        var transactions = await context.InvestmentTransactions.AsNoTracking()
            .Include(value => value.Instrument)
            .Include(value => value.Account)
            .OrderByDescending(value => value.TradeDate)
            .ThenByDescending(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        var overrides = await context.ManualPriceOverrides.AsNoTracking()
            .OrderByDescending(value => value.MarketDate)
            .ToListAsync(cancellationToken);

        var symbols = instruments
            .Where(value => !value.IsCustom && !string.IsNullOrWhiteSpace(value.ProviderSymbol))
            .Select(value => value.ProviderSymbol!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var priceBars = symbols.Count == 0
            ? []
            : await context.MarketPriceBars.AsNoTracking()
                .Where(value => value.Provider == "twelvedata" && symbols.Contains(value.Symbol))
                .ToListAsync(cancellationToken);
        var currencies = instruments.Select(value => value.Currency).Distinct().ToList();
        var fxBars = await context.FxRateBars.AsNoTracking()
            .Where(value => value.Provider == "twelvedata" &&
                            currencies.Contains(value.BaseCurrency) &&
                            value.QuoteCurrency == appCurrency)
            .ToListAsync(cancellationToken);

        var calculation = accounting.Calculate(transactions, appCurrency);
        var accountById = accounts.ToDictionary(value => value.Id);
        var instrumentById = instruments.ToDictionary(value => value.Id);
        var holdings = new List<InvestmentHoldingDto>();
        var warnings = calculation.Warnings.ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var position in calculation.Positions.Where(value => value.Units != 0))
        {
            if (!accountById.TryGetValue(position.AccountId, out var account) ||
                !instrumentById.TryGetValue(position.InstrumentId, out var instrument))
            {
                continue;
            }

            var prices = ResolvePrices(instrument, priceBars, overrides);
            var latest = prices.LastOrDefault();
            var previous = prices.Count > 1 ? prices[^2] : null;
            var fx = ResolveFx(instrument.Currency, appCurrency, today, fxBars, overrides, instrument.Id);
            decimal? valueNative = latest is null ? null : latest.Price * position.Units;
            decimal? valueApp = valueNative is not null && fx is not null ? valueNative * fx : null;
            decimal? unrealised = valueApp is not null && position.CostBasisApp is not null
                ? valueApp - position.CostBasisApp
                : null;
            decimal? daily = latest is not null && previous is not null && fx is not null
                ? (latest.Price - previous.Price) * position.Units * fx
                : null;
            var incomplete = instrument.Currency != appCurrency && fx is null;
            if (incomplete)
            {
                warnings.Add($"Current FX is missing for {instrument.Currency}/{appCurrency}; converted totals are incomplete.");
            }

            holdings.Add(new InvestmentHoldingDto(
                account.Id,
                account.Name,
                instrument.Id,
                instrument.Symbol,
                instrument.Name,
                instrument.Type,
                instrument.Currency,
                position.Units,
                position.Units == 0 ? 0 : position.CostBasisNative / position.Units,
                latest?.Price,
                valueNative,
                valueApp,
                daily,
                unrealised,
                unrealised is not null && position.CostBasisApp is > 0
                    ? unrealised / position.CostBasisApp.Value * 100m
                    : null,
                latest?.Date,
                latest?.FetchedAt,
                latest?.Manual == true,
                incomplete));
        }

        var convertedComplete = holdings.All(value => value.ValueApp is not null) &&
                                calculation.Positions.All(value =>
                                    value.CostBasisApp is not null &&
                                    value.RealisedApp is not null &&
                                    value.DividendsApp is not null);
        decimal? marketValue = convertedComplete ? holdings.Sum(value => value.ValueApp ?? 0) : null;
        decimal? costBasis = convertedComplete
            ? calculation.Positions.Where(value => value.Units != 0).Sum(value => value.CostBasisApp ?? 0)
            : null;
        decimal? realised = convertedComplete ? calculation.Positions.Sum(value => value.RealisedApp ?? 0) : null;
        decimal? dividends = convertedComplete ? calculation.Positions.Sum(value => value.DividendsApp ?? 0) : null;
        decimal? unrealisedTotal = marketValue is not null && costBasis is not null ? marketValue - costBasis : null;
        var growthLedger = await context.CycleBalances.AsNoTracking()
            .OrderByDescending(value => value.Year)
            .ThenByDescending(value => value.MonthIndex)
            .Select(value => (decimal?)value.GrowthBalance)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;

        var chart = BuildChart(range, transactions, instruments, priceBars, fxBars, overrides, appCurrency);
        var latestFetchedAt = holdings.Where(value => value.PriceFetchedAt is not null)
            .Select(value => value.PriceFetchedAt)
            .Max();
        var summary = new InvestmentSummaryDto(
            growthLedger,
            marketValue,
            costBasis,
            unrealisedTotal,
            unrealisedTotal is not null && costBasis is > 0 ? unrealisedTotal / costBasis * 100m : null,
            realised,
            dividends,
            convertedComplete ? holdings.Sum(value => value.DailyChangeApp ?? 0) : null);

        return new InvestmentPortfolioDto(
            appCurrency,
            summary,
            accounts,
            instruments,
            holdings.OrderByDescending(value => value.ValueApp ?? decimal.MinValue).ToList(),
            transactions.Select(ToDto).ToList(),
            overrides.Select(ToDto).ToList(),
            chart,
            BuildInsights(holdings, warnings),
            warnings.Distinct().ToList(),
            latestFetchedAt,
            provider.IsConfigured);
    }

    private IReadOnlyList<InvestmentChartPointDto> BuildChart(
        string range,
        IReadOnlyList<InvestmentTransaction> transactions,
        IReadOnlyList<InvestmentInstrument> instruments,
        IReadOnlyList<MarketPriceBar> priceBars,
        IReadOnlyList<FxRateBar> fxBars,
        IReadOnlyList<ManualPriceOverride> overrides,
        string appCurrency)
    {
        if (transactions.Count == 0) return [];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = range.ToLowerInvariant() switch
        {
            "1m" => today.AddMonths(-1),
            "3m" => today.AddMonths(-3),
            "6m" => today.AddMonths(-6),
            "1y" => today.AddYears(-1),
            _ => transactions.Min(value => value.TradeDate)
        };
        var dates = transactions.Select(value => value.TradeDate)
            .Concat(priceBars.Select(value => value.MarketDate))
            .Concat(overrides.Select(value => value.MarketDate))
            .Append(today)
            .Where(value => value >= from && value <= today)
            .Distinct()
            .Order()
            .ToList();
        if (dates.Count > 180)
        {
            var interval = (int)Math.Ceiling(dates.Count / 180m);
            dates = dates.Where((_, index) => index % interval == 0).Append(today).Distinct().Order().ToList();
        }

        var instrumentById = instruments.ToDictionary(value => value.Id);
        var points = new List<InvestmentChartPointDto>();
        foreach (var date in dates)
        {
            var relevant = transactions.Where(value => value.TradeDate <= date).ToList();
            var result = accounting.Calculate(relevant, appCurrency);
            decimal market = 0;
            decimal cost = 0;
            decimal contributions = 0;
            var complete = true;
            foreach (var position in result.Positions)
            {
                if (position.CostBasisApp is null || position.NetContributionsApp is null ||
                    !instrumentById.TryGetValue(position.InstrumentId, out var instrument))
                {
                    complete = false;
                    continue;
                }
                cost += position.CostBasisApp.Value;
                contributions += position.NetContributionsApp.Value;
                if (position.Units == 0) continue;
                var price = ResolvePrices(instrument, priceBars, overrides).LastOrDefault(value => value.Date <= date);
                var fx = ResolveFx(instrument.Currency, appCurrency, date, fxBars, overrides, instrument.Id);
                if (price is null || fx is null)
                {
                    complete = false;
                    continue;
                }
                market += price.Price * position.Units * fx.Value;
            }
            points.Add(new InvestmentChartPointDto(
                date,
                complete ? market : null,
                complete ? cost : null,
                complete ? contributions : null));
        }
        return points;
    }

    private static List<ResolvedPrice> ResolvePrices(
        InvestmentInstrument instrument,
        IReadOnlyList<MarketPriceBar> priceBars,
        IReadOnlyList<ManualPriceOverride> overrides)
    {
        var provider = priceBars
            .Where(value =>
                value.Symbol.Equals(instrument.ProviderSymbol, StringComparison.OrdinalIgnoreCase) &&
                value.Mic.Equals(instrument.ProviderMic ?? "", StringComparison.OrdinalIgnoreCase))
            .Select(value => new ResolvedPrice(value.MarketDate, value.Close, value.FetchedAt, false));
        var manual = overrides
            .Where(value => value.InstrumentId == instrument.Id)
            .Select(value => new ResolvedPrice(value.MarketDate, value.Price, value.UpdatedAt, true));
        return provider.Concat(manual)
            .GroupBy(value => value.Date)
            .Select(group => group.OrderByDescending(value => value.Manual).ThenByDescending(value => value.FetchedAt).First())
            .OrderBy(value => value.Date)
            .ToList();
    }

    private static decimal? ResolveFx(
        string nativeCurrency,
        string appCurrency,
        DateOnly date,
        IReadOnlyList<FxRateBar> fxBars,
        IReadOnlyList<ManualPriceOverride> overrides,
        Guid instrumentId)
    {
        if (nativeCurrency.Equals(appCurrency, StringComparison.OrdinalIgnoreCase)) return 1;
        var manual = overrides
            .Where(value => value.InstrumentId == instrumentId && value.MarketDate <= date && value.FxRate is > 0)
            .OrderByDescending(value => value.MarketDate)
            .FirstOrDefault();
        if (manual?.FxRate is > 0) return manual.FxRate;
        return fxBars
            .Where(value =>
                value.BaseCurrency == nativeCurrency &&
                value.QuoteCurrency == appCurrency &&
                value.MarketDate <= date)
            .OrderByDescending(value => value.MarketDate)
            .Select(value => (decimal?)value.Rate)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> BuildInsights(
        IReadOnlyList<InvestmentHoldingDto> holdings,
        IReadOnlyList<string> warnings)
    {
        if (holdings.Count == 0) return ["Add an opening position or transaction to begin portfolio analysis."];
        var insights = new List<string>();
        var valued = holdings.Where(value => value.ValueApp is not null).ToList();
        if (valued.Count > 0)
        {
            var total = valued.Sum(value => value.ValueApp ?? 0);
            var largest = valued.MaxBy(value => value.ValueApp);
            insights.Add($"Largest holding: {largest!.Symbol} ({(total > 0 ? largest.ValueApp / total * 100 : 0):0.#}% of valued holdings).");
            var performers = valued.Where(value => value.UnrealisedPercent is not null).ToList();
            if (performers.Count > 0)
            {
                insights.Add($"Best performer: {performers.MaxBy(value => value.UnrealisedPercent)!.Symbol}.");
                if (performers.Count > 1) insights.Add($"Weakest performer: {performers.MinBy(value => value.UnrealisedPercent)!.Symbol}.");
            }
            if (total > 0 && (largest.ValueApp ?? 0) / total >= 0.5m)
                insights.Add("Concentration: one holding represents at least half of the valued portfolio.");
        }
        if (holdings.Any(value => value.UsesManualPrice)) insights.Add("Manual prices are currently used for one or more holdings.");
        if (warnings.Any(value => value.Contains("FX", StringComparison.OrdinalIgnoreCase))) insights.Add("Converted totals are incomplete until missing FX rates are supplied.");
        if (holdings.Any(value => value.PriceDate is null || value.PriceDate < DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5))))
            insights.Add("Some holdings have stale or unavailable prices.");
        return insights;
    }

    public static InvestmentTransactionDto ToDto(InvestmentTransaction value) => new(
        value.Id, value.AccountId, value.InstrumentId, value.Type, value.TradeDate,
        value.Units, value.UnitPrice, value.CashAmount, value.Fees, value.Taxes,
        value.TradeFxRate, value.Notes, value.LinkedTransferId, value.CreatedAt);

    public static ManualPriceDto ToDto(ManualPriceOverride value) => new(
        value.Id, value.InstrumentId, value.MarketDate, value.Price, value.FxRate);

    private sealed record ResolvedPrice(DateOnly Date, decimal Price, DateTime FetchedAt, bool Manual);
}
