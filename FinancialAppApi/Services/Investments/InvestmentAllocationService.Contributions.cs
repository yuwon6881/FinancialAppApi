namespace FinancialAppApi.Services.Investments;

public sealed partial class InvestmentAllocationService
{
    /// <summary>
    /// Splits one routine deposit across the sleeves by cash-flow rebalancing: new money fills
    /// the gap to each sleeve's target first, and only what is left over is split by target
    /// weight. Two properties make this the right formula for the routine case:
    ///
    /// * On a perfectly balanced portfolio every gap equals target% x contribution, so the split
    ///   collapses to the plain target split — deposit 1000 against 33/33/34 and you are told to
    ///   buy 330/330/340.
    /// * When the mix has drifted, the same deposit leans toward whatever is underweight, so the
    ///   portfolio converges on target without selling anything (and without triggering tax).
    ///
    /// A routine on-track amount may be too small to close every gap, so its gaps are scaled
    /// proportionally. An off-track plan instead raises the amount to the exact no-sale total.
    /// </summary>
    internal static InvestmentContributionPlanDto? BuildContributionPlan(
        string appCurrency,
        decimal investedValue,
        IReadOnlyDictionary<string, decimal> values,
        IReadOnlyDictionary<string, decimal> targets,
        decimal minimumNewMoney,
        bool restoreTarget,
        decimal? usualGrowthDeposit,
        int cyclesObserved,
        decimal availableCash,
        IReadOnlyDictionary<string, string> labels)
    {
        // Cash already inside brokerage accounts participates in every plan. When the normal
        // cash plus deposit rhythm cannot restore the target, raise the total to the exact
        // no-sale amount instead of falling back to a separate sell-and-rebuy checklist.
        var routineNewMoney = usualGrowthDeposit is > 0 ? usualGrowthDeposit.Value : 0;
        var readyToInvest = availableCash + routineNewMoney;
        var amount = restoreTarget ? Math.Max(readyToInvest, minimumNewMoney) : readyToInvest;
        if (amount <= 0.005m) return null;
        var isEstimated = usualGrowthDeposit is not > 0;

        var projectedTotal = investedValue + amount;
        var gaps = targets.Keys.ToDictionary(
            key => key,
            key => Math.Max(0, projectedTotal * targets[key] / 100m - values[key]));
        var totalGap = gaps.Values.Sum();

        var allocations = targets.Keys.ToDictionary(key => key, _ => 0m);
        if (totalGap <= 0.005m)
        {
            // Every sleeve is at or above its target share of the larger portfolio, which can only
            // happen from rounding. Fall back to the plain target split.
            foreach (var key in targets.Keys)
                allocations[key] = amount * targets[key] / 100m;
        }
        else if (totalGap >= amount)
        {
            foreach (var key in targets.Keys)
                allocations[key] = amount * gaps[key] / totalGap;
        }
        else
        {
            var leftover = amount - totalGap;
            foreach (var key in targets.Keys)
                allocations[key] = gaps[key] + leftover * targets[key] / 100m;
        }

        // Round largest first without taking any group's allocation below zero.
        var rounded = targets.Keys.ToDictionary(
            key => key,
            key => RoundMoney(allocations[key]));
        var drift = RoundMoney(amount) - rounded.Values.Sum();
        if (drift != 0)
        {
            foreach (var key in targets.Keys.OrderByDescending(key => rounded[key]))
            {
                var correction = Math.Max(drift, -rounded[key]);
                rounded[key] += correction;
                drift -= correction;
                if (drift == 0) break;
            }
        }

        var sleeves = targets.Keys.Select(key =>
        {
            var projectedValue = values[key] + rounded[key];
            var projectedPercentage = projectedTotal <= 0 ? 0 : projectedValue / projectedTotal * 100m;
            return new InvestmentContributionSleeveDto(
                key,
                labels[key],
                rounded[key],
                RoundMoney(amount) <= 0 ? 0 : Math.Round(rounded[key] / RoundMoney(amount) * 100m, 1),
                Math.Round(projectedPercentage, 2),
                Math.Round(projectedPercentage - targets[key], 2));
        }).ToList();

        var depositBasis = cyclesObserved == 1
            ? "your Growth deposit from the last completed cycle"
            : $"the median of your Growth deposits across {cyclesObserved} completed cycles";
        string basis;
        if (restoreTarget && minimumNewMoney > readyToInvest + 0.005m)
        {
            var newMoney = Math.Max(0, amount - availableCash);
            basis = availableCash > 0
                ? $"The total needed to restore your target without selling. It includes {Format(availableCash, appCurrency)} of uninvested cash already in your brokerage accounts; the remaining {Format(newMoney, appCurrency)} is new money."
                : "The total new money needed to restore your target without selling.";
        }
        else if (availableCash > 0 && usualGrowthDeposit is > 0)
        {
            basis = $"Includes {Format(availableCash, appCurrency)} of uninvested cash already in your brokerage accounts plus {depositBasis}.";
        }
        else if (availableCash > 0)
        {
            basis = "Uninvested cash already in your brokerage accounts.";
        }
        else
        {
            basis = char.ToUpperInvariant(depositBasis[0]) + depositBasis[1..] + ".";
        }

        return new InvestmentContributionPlanDto(
            RoundMoney(amount), usualGrowthDeposit is > 0 ? RoundMoney(usualGrowthDeposit.Value) : null,
            basis, cyclesObserved, isEstimated, sleeves);
    }
}
