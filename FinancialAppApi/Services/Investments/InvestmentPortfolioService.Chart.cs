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
        string appCurrency,
        IReadOnlyList<InvestmentPeriodSnapshot> settled)
    {
        if (transactions.Count == 0 && cashFlows.Count == 0) return [];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = InvestmentChartRange.StartFor(range, today)
            ?? transactions.Select(value => value.TradeDate)
                .Concat(cashFlows.Select(value => value.Date))
                .Min();

        // Settled months come from the month-end cache; only the days after the newest one are
        // valued here, starting from the positions and cash it carried. Without a usable cache
        // the walk starts from nothing and values every date, as it always did.
        var points = new List<InvestmentChartPointDto>();
        var opening = ChartOpening.Empty;
        var resume = settled.Count > 0 ? InvestmentCarryState.TryParse(settled[^1].CarryState) : null;
        if (resume is not null)
        {
            decimal? deposits = 0;
            foreach (var month in settled)
            {
                deposits += month.NetContributions;
                if (month.PeriodEnd >= from) points.Add(new InvestmentChartPointDto(month.PeriodEnd, month.ClosingValue, deposits));
            }
            opening = new ChartOpening(settled[^1].PeriodEnd, resume, deposits);
        }

        var dates = transactions.Select(value => value.TradeDate)
            .Concat(cashFlows.Select(value => value.Date))
            .Concat(priceBars.Select(value => value.MarketDate))
            .Append(today)
            .Where(value => value >= from && value > opening.Date && value <= today)
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
        var laterTransactions = transactions.Where(value => value.TradeDate > opening.Date).ToList();
        var laterFlows = cashFlows.Where(value => value.Date > opening.Date).ToList();
        foreach (var date in dates)
        {
            var relevant = laterTransactions.Where(value => value.TradeDate <= date).ToList();
            var result = accounting.Calculate(relevant, appCurrency, HistoricalTradeFx, opening.Carry.Positions);
            decimal market = 0;
            decimal cash = 0;
            decimal? deposits = opening.Deposits;
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
            var cashNative = opening.Carry.Cash.ToDictionary(value => (value.AccountId, value.Currency), value => value.Amount);
            void AddCash(Guid accountId, string currency, decimal amount)
            {
                var key = (accountId, currency);
                cashNative[key] = cashNative.GetValueOrDefault(key) + amount;
            }
            foreach (var flow in laterFlows.Where(value => value.Date <= date))
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
                if (balance.Value == 0) continue;
                var cashFx = seriesResolver.ResolveFx(balance.Key.Currency, appCurrency, date, fxBars);
                if (cashFx is null) complete = false;
                else cash += balance.Value * cashFx.Rate;
            }
            points.Add(new InvestmentChartPointDto(
                date,
                complete ? market + cash : null,
                complete ? deposits : null));
        }
        // Decades of month-ends would outgrow the cap on their own; thin the whole series.
        return InvestmentChartRange.Sample(points);
    }

    /// <summary>Where the day-by-day walk starts: nothing, or the newest settled month-end.</summary>
    private sealed record ChartOpening(DateOnly Date, InvestmentCarryState Carry, decimal? Deposits)
    {
        public static readonly ChartOpening Empty = new(DateOnly.MinValue, InvestmentCarryState.Empty, 0);
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
