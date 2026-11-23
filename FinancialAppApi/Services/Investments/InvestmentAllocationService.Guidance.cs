namespace FinancialAppApi.Services.Investments;

public sealed partial class InvestmentAllocationService
{
    private static IReadOnlyList<InvestmentAllocationRecommendationDto> BuildRecommendations(
        string currency,
        decimal investedValue,
        IReadOnlyDictionary<string, decimal> values,
        IReadOnlyDictionary<string, decimal> targets,
        decimal cash,
        decimal? usualGrowthDeposit,
        decimal watchDrift,
        IReadOnlyDictionary<string, string> labels)
    {
        var recommendations = new List<InvestmentAllocationRecommendationDto>();
        var expectedGrowthDeposit = usualGrowthDeposit ?? 0;
        var funding = Math.Max(0, cash) + expectedGrowthDeposit;
        var priority = 1;

        if (usualGrowthDeposit is > 0)
        {
            recommendations.Add(new InvestmentAllocationRecommendationDto(
                priority++, "TopUp", null, RoundMoney(expectedGrowthDeposit),
                $"Use the usual completed-cycle Growth deposit of {Format(expectedGrowthDeposit, currency)} before considering any sale."));
        }
        if (cash > 0)
        {
            recommendations.Add(new InvestmentAllocationRecommendationDto(
                priority++, "UseCash", null, RoundMoney(cash),
                $"Invest {Format(cash, currency)} of available cash before selling any holding."));
        }

        // Model all expected new money as invested into the largest shortfalls first, so a sale
        // appears only when cash-flow rebalancing still cannot bring the mix inside the band.
        var projectedTotal = investedValue + funding;
        var projected = values.ToDictionary(value => value.Key, value => value.Value);
        var remainingFunding = funding;
        var deficits = targets.Keys
            .Select(key => new
            {
                Key = key,
                Label = labels[key],
                Amount = Math.Max(0, projectedTotal * targets[key] / 100m - projected[key]),
                Drift = projected[key] / projectedTotal * 100m - targets[key]
            })
            .Where(value => value.Amount > 0.005m)
            .OrderBy(value => value.Drift)
            .ToList();
        foreach (var deficit in deficits)
        {
            if (remainingFunding <= 0.005m) break;
            var amount = Math.Min(deficit.Amount, remainingFunding);
            if (amount <= 0.005m) continue;
            projected[deficit.Key] += amount;
            remainingFunding -= amount;
            recommendations.Add(new InvestmentAllocationRecommendationDto(
                priority++, "Buy", deficit.Key, RoundMoney(amount),
                $"Buy {Format(amount, currency)} of {deficit.Label} with new money."));
        }

        var stillOutsideBand = targets.Keys.Any(key =>
            Math.Abs(projected[key] / projectedTotal * 100m - targets[key]) >= watchDrift);
        if (!stillOutsideBand) return recommendations;

        var saleAmounts = targets.Keys.Select(key => new
        {
            Key = key,
            Label = labels[key],
            Amount = Math.Max(0, projected[key] - projectedTotal * targets[key] / 100m)
        }).Where(value => value.Amount > 0.005m).ToList();
        foreach (var sale in saleAmounts)
        {
            recommendations.Add(new InvestmentAllocationRecommendationDto(
                priority++, "Sell", sale.Key, RoundMoney(sale.Amount),
                $"Only after investing new money, sell {Format(sale.Amount, currency)} of {sale.Label}."));
        }
        foreach (var key in targets.Keys)
        {
            var difference = projectedTotal * targets[key] / 100m - projected[key];
            if (difference <= 0.005m) continue;
            recommendations.Add(new InvestmentAllocationRecommendationDto(
                priority++, "TransferBuy", key, RoundMoney(difference),
                $"Reinvest {Format(difference, currency)} of sale proceeds into {labels[key]}."));
        }
        return recommendations;
    }

    // A median keeps one windfall cycle from inflating the routine amount shown every month.
    private static (decimal? Amount, int Cycles) UsualCompletedCycleContribution(
        IReadOnlyList<InvestmentContributionDto> contributions,
        int cycleDay)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentCycle = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
        var cycleTotals = contributions
            .Where(value => value.AmountApp > 0)
            .Select(value => new
            {
                Cycle = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(value.Date, cycleDay),
                value.AmountApp
            })
            .Where(value => value.Cycle != currentCycle)
            .GroupBy(value => value.Cycle)
            .Select(group => group.Sum(value => value.AmountApp))
            .OrderBy(value => value)
            .ToList();

        if (cycleTotals.Count == 0) return (null, 0);
        var middle = cycleTotals.Count / 2;
        return (RoundMoney(cycleTotals.Count % 2 == 1
            ? cycleTotals[middle]
            : (cycleTotals[middle - 1] + cycleTotals[middle]) / 2m), cycleTotals.Count);
    }
}
