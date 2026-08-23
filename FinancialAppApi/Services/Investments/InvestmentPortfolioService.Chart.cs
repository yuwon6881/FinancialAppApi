using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

public partial class InvestmentPortfolioService
{
    private IReadOnlyList<InvestmentChartPointDto> BuildChart(
        string range,
        IReadOnlyList<InvestmentTransaction> transactions,
        IReadOnlyList<InvestmentCashFlow> cashFlows,
        IReadOnlyList<InvestmentInstrument> instruments,
        IReadOnlyDictionary<Guid, MarketInstrumentReference> references,
        IReadOnlyList<MarketPriceBar> priceBars,
        IReadOnlyList<FxRateBar> fxBars,
        string appCurrency)
    {
        if (transactions.Count == 0 && cashFlows.Count == 0) return [];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = InvestmentChartRange.StartFor(range, today)
            ?? transactions.Select(value => value.TradeDate)
                .Concat(cashFlows.Select(value => value.Date))
                .Min();
        var dates = transactions.Select(value => value.TradeDate)
            .Concat(cashFlows.Select(value => value.Date))
            .Concat(priceBars.Select(value => value.MarketDate))
            .Append(today)
            .Where(value => value >= from && value <= today)
            .Distinct()
            .Order()
            .ToList();
        dates = InvestmentChartRange.Sample(dates);

        var instrumentById = instruments.ToDictionary(value => value.Id);
        // Resolved once per instrument rather than once per (date, position): this loop runs for
        // every charted date, and re-filtering the whole price-bar table inside it is what made
        // long ranges expensive as history grew.
        var pricesByInstrument = instruments.ToDictionary(
            value => value.Id,
            value => seriesResolver.ResolvePrices(references.GetValueOrDefault(value.Id), priceBars));
        decimal? HistoricalTradeFx(InvestmentTransaction transaction) =>
            seriesResolver.ResolveFx(transaction.Instrument.Currency, appCurrency, transaction.TradeDate, fxBars)?.Rate;
        var points = new List<InvestmentChartPointDto>();
        foreach (var date in dates)
        {
            var relevant = transactions.Where(value => value.TradeDate <= date);
            var result = accounting.Calculate(relevant, appCurrency, HistoricalTradeFx);
            decimal market = 0;
            decimal cash = 0;
            decimal deposits = 0;
            var complete = true;
            foreach (var position in result.Positions)
            {
                if (!instrumentById.TryGetValue(position.InstrumentId, out var instrument))
                {
                    complete = false;
                    continue;
                }
                if (position.Units == 0) continue;
                var price = pricesByInstrument[instrument.Id].LastOrDefault(value => value.Date <= date);
                var fx = seriesResolver.ResolveFx(instrument.Currency, appCurrency, date, fxBars);
                if (price is null || fx is null)
                {
                    complete = false;
                    continue;
                }
                market += price.Price * position.Units * fx.Rate;
            }
            var cashNative = new Dictionary<(Guid AccountId, string Currency), decimal>();
            void AddCash(Guid accountId, string currency, decimal amount)
            {
                var key = (accountId, currency);
                cashNative[key] = cashNative.GetValueOrDefault(key) + amount;
            }
            foreach (var flow in cashFlows.Where(value => value.Date <= date))
            {
                AddCash(flow.AccountId, flow.Currency, flow.Amount);
                if (flow.ToCurrency is not null && flow.ToAmount is not null)
                    AddCash(flow.AccountId, flow.ToCurrency, flow.ToAmount.Value);
                // Conversions are value-neutral and never count as deposits.
                if (IsConversion(flow)) continue;
                var flowFx = seriesResolver.ResolveFx(flow.Currency, appCurrency, flow.Date, fxBars);
                if (flowFx is null) complete = false;
                else deposits += flow.Amount * flowFx.Rate;
            }
            foreach (var transaction in relevant)
            {
                var currency = transaction.Instrument.Currency;
                var charges = transaction.Fees + transaction.Taxes;
                if (transaction.Type == "Buy") AddCash(transaction.AccountId, currency, -((transaction.CashAmount ?? 0) + charges));
                if (transaction.Type == "Sell" || transaction.Type == "Dividend")
                    AddCash(transaction.AccountId, currency, (transaction.CashAmount ?? 0) - charges);
                if (transaction.Type == "FeeTax") AddCash(transaction.AccountId, currency, -((transaction.CashAmount ?? 0) + charges));
            }
            foreach (var balance in cashNative)
            {
                var cashFx = seriesResolver.ResolveFx(balance.Key.Currency, appCurrency, date, fxBars);
                if (cashFx is null) complete = false;
                else cash += balance.Value * cashFx.Rate;
            }
            points.Add(new InvestmentChartPointDto(
                date,
                complete ? market + cash : null,
                complete ? deposits : null));
        }
        return points;
    }

    private static decimal RoundMoney(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static IReadOnlyList<string> BuildInsights(
        IReadOnlyList<InvestmentHoldingDto> holdings,
        IReadOnlyList<string> warnings)
    {
        if (holdings.Count == 0) return ["Add a buy transaction to begin portfolio analysis."];
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
        if (warnings.Any(value => value.Contains("FX", StringComparison.OrdinalIgnoreCase))) insights.Add("Converted totals are incomplete until missing FX rates are supplied.");
        if (holdings.Any(value => value.PriceDate is null || value.PriceDate < DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5))))
            insights.Add("Some holdings have stale or unavailable prices.");
        return insights;
    }

    public static InvestmentTransactionDto ToDto(InvestmentTransaction value) => new(
        value.Id, value.AccountId, value.InstrumentId, value.Type, value.TradeDate,
        value.Units, value.UnitPrice, value.CashAmount, value.Fees, value.Taxes,
        value.CreatedAt);

    public static InvestmentCashFlowDto ToDto(InvestmentCashFlow value) => new(
        value.Id, value.AccountId, value.Currency, value.Type, value.Amount, value.Date,
        value.ToCurrency, value.ToAmount, value.CreatedAt);

    internal static bool IsConversion(InvestmentCashFlow value)
        => value.Type.Equals("Conversion", StringComparison.OrdinalIgnoreCase);
}
