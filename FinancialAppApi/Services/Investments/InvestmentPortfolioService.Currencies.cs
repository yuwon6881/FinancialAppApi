namespace FinancialAppApi.Services.Investments;

public sealed partial class InvestmentPortfolioService
{
    private static decimal? CalculateAnnualReturn(
        IEnumerable<(DateOnly Date, string Currency, decimal Amount)> contributionLegs,
        DateOnly today,
        decimal? totalValue,
        Func<string, decimal?, decimal?> toApp)
    {
        if (totalValue is not { } endingValue) return null;

        // Same-day legs in one currency share a rate and cancel before conversion, even when
        // that currency has no current rate.
        var flows = new List<DatedInvestmentFlow>();
        foreach (var day in contributionLegs.GroupBy(leg => (leg.Date, leg.Currency)))
        {
            if (toApp(day.Key.Currency, -day.Sum(leg => leg.Amount)) is not { } amount) return null;
            flows.Add(new DatedInvestmentFlow(day.Key.Date, amount));
        }

        flows.Add(new DatedInvestmentFlow(today, endingValue));
        return InvestmentReturnCalculator.Calculate(flows);
    }

    /// <summary>
    /// The portfolio split by currency, each part in its own currency. A currency appears once it
    /// has held money, a position, or a result; one where everything has gone back to zero drops out.
    /// </summary>
    private static IReadOnlyList<InvestmentCurrencySummaryDto> BuildCurrencySummaries(
        IReadOnlyList<InvestmentHoldingDto> holdings,
        IEnumerable<(CalculatedPosition Position, string Currency)> positions,
        IReadOnlyList<InvestmentCashBalanceDto> cashBalances,
        IEnumerable<(string Currency, decimal Amount)> contributionLegs,
        Func<string, ResolvedMarketFx?> todayFx)
    {
        var positionList = positions.ToList();
        var deposits = contributionLegs
            .GroupBy(value => value.Currency, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(value => value.Amount), StringComparer.OrdinalIgnoreCase);
        var currencies = holdings.Select(value => value.Currency)
            .Concat(positionList.Select(value => value.Currency))
            .Concat(cashBalances.Select(value => value.Currency))
            .Concat(deposits.Keys)
            .Select(value => value.ToUpperInvariant())
            .Distinct()
            .Order(StringComparer.Ordinal);

        var summaries = new List<InvestmentCurrencySummaryDto>();
        foreach (var currency in currencies)
        {
            bool Same(string value) => value.Equals(currency, StringComparison.OrdinalIgnoreCase);
            var held = holdings.Where(value => Same(value.Currency)).ToList();
            var owned = positionList.Where(value => Same(value.Currency)).Select(value => value.Position).ToList();
            decimal? market = held.All(value => value.ValueNative is not null) ? held.Sum(value => value.ValueNative ?? 0) : null;
            var cash = cashBalances.Where(value => Same(value.Currency)).Sum(value => value.Amount);
            var costBasis = owned.Where(value => value.Units != 0).Sum(value => value.CostBasisNative);
            var realised = owned.Sum(value => value.RealisedNative);
            var dividends = owned.Sum(value => value.DividendsNative);
            var netDeposits = deposits.GetValueOrDefault(currency);
            if (held.Count == 0 && cash == 0 && costBasis == 0 && realised == 0 && dividends == 0 && netDeposits == 0)
                continue;
            var unrealised = market - costBasis;
            var fx = todayFx(currency);
            summaries.Add(new InvestmentCurrencySummaryDto(
                currency,
                fx?.Rate,
                fx?.Date,
                market,
                cash,
                market + cash,
                RoundMoney(netDeposits),
                costBasis,
                unrealised,
                unrealised is not null && costBasis > 0 ? unrealised / costBasis * 100m : null,
                realised,
                dividends,
                held.All(value => value.DailyChangeNative is not null) ? held.Sum(value => value.DailyChangeNative ?? 0) : null));
        }
        return summaries;
    }
}
