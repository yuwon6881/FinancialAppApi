using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

public sealed record InvestmentPlanDto(
    Guid? Id,
    decimal UsEquityTarget,
    decimal InternationalExUsTarget,
    decimal BondsTarget,
    decimal WatchDrift,
    decimal AlertDrift,
    DateTime? UpdatedAt);

public sealed record InvestmentAllocationAssignmentDto(
    Guid InstrumentId,
    string Symbol,
    string Name,
    string? Sleeve,
    int Order);

public sealed record InvestmentSleeveAllocationDto(
    string Sleeve,
    string Label,
    decimal TargetPercentage,
    decimal? CurrentPercentage,
    decimal? Value,
    decimal? DriftPercentagePoints,
    decimal? DriftAmount,
    string Status);

public sealed record InvestmentAllocationRecommendationDto(
    int Priority,
    string Kind,
    string? Sleeve,
    decimal Amount,
    string Message);

public sealed record InvestmentContributionSleeveDto(
    string Sleeve,
    string Label,
    decimal Amount,
    decimal PercentageOfContribution,
    decimal ProjectedPercentage,
    decimal ProjectedDriftPercentagePoints);

/// <summary>
/// How to split the next routine Growth deposit across the three sleeves so the
/// portfolio keeps (or moves back toward) its target mix without selling anything.
/// Unlike <see cref="InvestmentAllocationOverviewDto.Recommendations"/> this is
/// produced even when the plan is on track, because the routine question — "I am
/// depositing my usual amount, how much of each do I buy?" — still has an answer.
/// </summary>
public sealed record InvestmentContributionPlanDto(
    decimal Amount,
    string Basis,
    int CyclesObserved,
    bool IsEstimated,
    IReadOnlyList<InvestmentContributionSleeveDto> Sleeves);

public sealed record InvestmentMarketDataFreshnessDto(
    DateTime? AsOf,
    bool IsStale,
    bool HasMissingData,
    int MaxAgeMinutes,
    IReadOnlyList<string> StaleInputs);

public sealed record InvestmentAllocationOverviewDto(
    string Status,
    string AppCurrency,
    InvestmentPlanDto Plan,
    IReadOnlyList<InvestmentAllocationAssignmentDto> Assignments,
    IReadOnlyList<InvestmentSleeveAllocationDto> Sleeves,
    IReadOnlyList<InvestmentAllocationRecommendationDto> Recommendations,
    IReadOnlyList<string> IncompleteReasons,
    InvestmentMarketDataFreshnessDto Freshness,
    decimal? InvestedValue,
    decimal AvailableCash,
    decimal? MinimumContribution,
    InvestmentContributionPlanDto? ContributionPlan = null);

public sealed class InvestmentAllocationService(AppDbContext context)
{
    public const int AutomaticRefreshMinutes = 60;

    private static readonly (string Key, string Label)[] SleeveDefinitions =
    [
        ("USEquity", "US Equity"),
        ("InternationalExUS", "International ex-US"),
        ("Bonds", "Bonds")
    ];

    public async Task<InvestmentAllocationOverviewDto> BuildAsync(
        string appCurrency,
        IReadOnlyList<InvestmentHoldingDto> holdings,
        IReadOnlyList<InvestmentInstrumentSetupDto> instruments,
        IReadOnlyList<InvestmentCashBalanceDto> cashBalances,
        IReadOnlyList<InvestmentContributionDto> contributions,
        bool marketDataConfigured,
        CancellationToken cancellationToken)
    {
        var planEntity = await context.InvestmentPlans.AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        var plan = ToDto(planEntity);
        var assignments = instruments.Select(value => new InvestmentAllocationAssignmentDto(
                value.Id, value.Symbol, value.Name, value.AllocationSleeve, value.AllocationOrder))
            .OrderBy(value => value.Order)
            .ThenBy(value => value.Symbol)
            .ToList();
        var reasons = new List<string>();
        var freshnessInputs = new List<(string Label, DateTime? FetchedAt)>();

        foreach (var holding in holdings)
        {
            var assignment = instruments.FirstOrDefault(value => value.Id == holding.InstrumentId);
            if (assignment?.AllocationSleeve is null)
                reasons.Add($"{holding.Symbol} must be assigned to an investment-plan sleeve.");
            if (holding.LatestPriceNative is null)
                reasons.Add($"{holding.Symbol} does not have a current price.");
            if (holding.ValueApp is null)
                reasons.Add($"{holding.Symbol} cannot be valued in {appCurrency}.");

            if (!holding.UsesManualPrice)
                freshnessInputs.Add(($"{holding.Symbol} price", holding.PriceFetchedAt));
            if (!holding.Currency.Equals(appCurrency, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(holding.FxSource, "Manual FX", StringComparison.OrdinalIgnoreCase))
                freshnessInputs.Add(($"{holding.Currency}/{appCurrency} FX", holding.FxFetchedAt));
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-AutomaticRefreshMinutes);
        var staleInputs = freshnessInputs
            .Where(value => value.FetchedAt is null || value.FetchedAt < cutoff)
            .Select(value => value.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var freshness = new InvestmentMarketDataFreshnessDto(
            freshnessInputs.Where(value => value.FetchedAt is not null)
                .Select(value => value.FetchedAt)
                .DefaultIfEmpty(null)
                .Min(),
            staleInputs.Count > 0,
            freshnessInputs.Any(value => value.FetchedAt is null),
            AutomaticRefreshMinutes,
            staleInputs);

        if (holdings.Count == 0)
        {
            return new InvestmentAllocationOverviewDto(
                "NotStarted", appCurrency, plan, assignments,
                EmptySleeves(plan), [], [], freshness, 0, PositiveCash(cashBalances), 0);
        }

        var incompleteReasons = reasons.Distinct().ToList();
        if (incompleteReasons.Count > 0)
        {
            return new InvestmentAllocationOverviewDto(
                "Incomplete", appCurrency, plan, assignments,
                IncompleteSleeves(plan, holdings, instruments), [], incompleteReasons,
                freshness, null, PositiveCash(cashBalances), null);
        }

        var investedValue = holdings.Sum(value => value.ValueApp!.Value);
        if (investedValue <= 0)
        {
            return new InvestmentAllocationOverviewDto(
                "NotStarted", appCurrency, plan, assignments,
                EmptySleeves(plan), [], [], freshness, investedValue, PositiveCash(cashBalances), 0);
        }

        var targets = Targets(plan);
        var values = SleeveDefinitions.ToDictionary(
            definition => definition.Key,
            definition => holdings.Where(holding =>
                    instruments.First(instrument => instrument.Id == holding.InstrumentId)
                        .AllocationSleeve == definition.Key)
                .Sum(holding => holding.ValueApp!.Value));
        var sleeves = BuildValuedSleeves(plan, investedValue, values);
        var status = sleeves.Any(value => value.Status == "Alert")
            ? "Alert"
            : sleeves.Any(value => value.Status == "Watch") ? "Watch" : "OnTrack";
        var availableCash = PositiveCash(cashBalances);
        var cycleDay = await context.FinancialSettings.AsNoTracking()
            .Select(value => (int?)value.CycleDay)
            .SingleOrDefaultAsync(cancellationToken) ?? 28;
        var (usualGrowthDeposit, cyclesObserved) = UsualCompletedCycleContribution(contributions, cycleDay);

        var minimumNewMoney = SleeveDefinitions.Max(definition =>
            values[definition.Key] / (targets[definition.Key] / 100m) - investedValue);
        minimumNewMoney = Math.Max(0, minimumNewMoney);

        var recommendations = status == "OnTrack" 
            ? new List<InvestmentAllocationRecommendationDto>()
            : BuildRecommendations(
                appCurrency, investedValue, values, targets, availableCash, usualGrowthDeposit,
                plan.WatchDrift);

        return new InvestmentAllocationOverviewDto(
            status, appCurrency, plan, assignments, sleeves, recommendations, [],
            freshness, RoundMoney(investedValue), RoundMoney(availableCash),
            RoundMoney(Math.Max(0, minimumNewMoney - availableCash - (usualGrowthDeposit ?? 0))),
            BuildContributionPlan(
                investedValue, values, targets, usualGrowthDeposit, cyclesObserved, availableCash));
    }

    public static string? ValidatePlan(InvestmentPlanMutationDto value)
    {
        if (value.UsEquityTarget <= 0 || value.InternationalExUsTarget <= 0 || value.BondsTarget <= 0)
            return "Every sleeve target must be greater than zero.";
        if (value.UsEquityTarget + value.InternationalExUsTarget + value.BondsTarget != 100)
            return "Investment-plan targets must total exactly 100%.";
        if (value.WatchDrift <= 0 || value.AlertDrift <= value.WatchDrift || value.AlertDrift > 100)
            return "Alert drift must be greater than Watch drift, and both bands must be valid.";
        return null;
    }

    internal static string ClassifyDrift(decimal absoluteDrift, decimal watchDrift, decimal alertDrift)
        => absoluteDrift >= alertDrift ? "Alert" : absoluteDrift >= watchDrift ? "Watch" : "OnTrack";

    public static InvestmentPlanDto ToDto(InvestmentPlan? value) => value is null
        ? new InvestmentPlanDto(null, 66, 10, 24, 3, 5, null)
        : new InvestmentPlanDto(
            value.Id, value.UsEquityTarget, value.InternationalExUsTarget, value.BondsTarget,
            value.WatchDrift, value.AlertDrift, value.UpdatedAt);

    private static IReadOnlyList<InvestmentSleeveAllocationDto> EmptySleeves(InvestmentPlanDto plan)
        => SleeveDefinitions.Select(value => new InvestmentSleeveAllocationDto(
            value.Key, value.Label, Targets(plan)[value.Key], null, 0, null, null, "NotStarted")).ToList();

    private static IReadOnlyList<InvestmentSleeveAllocationDto> IncompleteSleeves(
        InvestmentPlanDto plan,
        IReadOnlyList<InvestmentHoldingDto> holdings,
        IReadOnlyList<InvestmentInstrumentSetupDto> instruments)
        => SleeveDefinitions.Select(value => new InvestmentSleeveAllocationDto(
            value.Key, value.Label, Targets(plan)[value.Key], null,
            holdings.Where(holding => holding.ValueApp is not null &&
                    instruments.First(instrument => instrument.Id == holding.InstrumentId).AllocationSleeve == value.Key)
                .Sum(holding => holding.ValueApp ?? 0),
            null, null, "Incomplete")).ToList();

    private static IReadOnlyList<InvestmentSleeveAllocationDto> BuildValuedSleeves(
        InvestmentPlanDto plan,
        decimal total,
        IReadOnlyDictionary<string, decimal> values)
    {
        var targets = Targets(plan);
        return SleeveDefinitions.Select(definition =>
        {
            var percentage = values[definition.Key] / total * 100m;
            var drift = percentage - targets[definition.Key];
            var absolute = Math.Abs(drift);
            var status = ClassifyDrift(absolute, plan.WatchDrift, plan.AlertDrift);
            return new InvestmentSleeveAllocationDto(
                definition.Key, definition.Label, targets[definition.Key],
                Math.Round(percentage, 2), RoundMoney(values[definition.Key]), Math.Round(drift, 2),
                RoundMoney(values[definition.Key] - total * targets[definition.Key] / 100m), status);
        }).ToList();
    }

    private static IReadOnlyList<InvestmentAllocationRecommendationDto> BuildRecommendations(
        string currency,
        decimal investedValue,
        IReadOnlyDictionary<string, decimal> values,
        IReadOnlyDictionary<string, decimal> targets,
        decimal cash,
        decimal? usualGrowthDeposit,
        decimal watchDrift)
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

        // Apply all expected new money to the most underweight sleeves first. This models the
        // portfolio after the user's normal Growth deposit and available cash have been invested,
        // so a sale is only recommended if a configured Watch/Alert drift would still remain.
        var projectedTotal = investedValue + funding;
        var projected = values.ToDictionary(value => value.Key, value => value.Value);
        var remainingFunding = funding;
        var deficits = SleeveDefinitions
            .Select(definition => new
            {
                definition.Key,
                definition.Label,
                Amount = Math.Max(0, projectedTotal * targets[definition.Key] / 100m - projected[definition.Key]),
                Drift = projected[definition.Key] / projectedTotal * 100m - targets[definition.Key]
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

        var stillOutsideBand = SleeveDefinitions.Any(definition =>
            Math.Abs(projected[definition.Key] / projectedTotal * 100m - targets[definition.Key]) >= watchDrift);

        if (stillOutsideBand)
        {
            var saleAmounts = SleeveDefinitions.Select(definition => new
            {
                definition.Key,
                definition.Label,
                Amount = Math.Max(0, projected[definition.Key] - projectedTotal * targets[definition.Key] / 100m)
            }).Where(value => value.Amount > 0.005m).ToList();
            foreach (var sale in saleAmounts)
            {
                recommendations.Add(new InvestmentAllocationRecommendationDto(
                    priority++, "Sell", sale.Key, RoundMoney(sale.Amount),
                    $"Only after investing new money, sell {Format(sale.Amount, currency)} of {sale.Label}."));
            }
            foreach (var definition in SleeveDefinitions)
            {
                var difference = projectedTotal * targets[definition.Key] / 100m - projected[definition.Key];
                if (difference <= 0.005m) continue;
                recommendations.Add(new InvestmentAllocationRecommendationDto(
                    priority++, "TransferBuy", definition.Key, RoundMoney(difference),
                    $"Reinvest {Format(difference, currency)} of sale proceeds into {definition.Label}."));
            }
        }

        return recommendations;
    }

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
    /// When the deposit is too small to close every gap the gaps are scaled proportionally rather
    /// than filled greedily: routine buying should keep feeding all three sleeves, and the greedy
    /// "most underweight first" ordering already exists in the rebalancing recommendations.
    /// </summary>
    private static InvestmentContributionPlanDto? BuildContributionPlan(
        decimal investedValue,
        IReadOnlyDictionary<string, decimal> values,
        IReadOnlyDictionary<string, decimal> targets,
        decimal? usualGrowthDeposit,
        int cyclesObserved,
        decimal availableCash)
    {
        // Prefer the user's own deposit rhythm; fall back to cash already sitting in the
        // brokerage, which is the only other amount we can honestly say is ready to invest.
        var amount = usualGrowthDeposit is > 0 ? usualGrowthDeposit.Value : availableCash;
        if (amount <= 0.005m) return null;
        var isEstimated = usualGrowthDeposit is not > 0;

        var projectedTotal = investedValue + amount;
        var gaps = SleeveDefinitions.ToDictionary(
            definition => definition.Key,
            definition => Math.Max(0, projectedTotal * targets[definition.Key] / 100m - values[definition.Key]));
        var totalGap = gaps.Values.Sum();

        var allocations = SleeveDefinitions.ToDictionary(definition => definition.Key, _ => 0m);
        if (totalGap <= 0.005m)
        {
            // Every sleeve is at or above its target share of the larger portfolio, which can only
            // happen from rounding. Fall back to the plain target split.
            foreach (var definition in SleeveDefinitions)
                allocations[definition.Key] = amount * targets[definition.Key] / 100m;
        }
        else if (totalGap >= amount)
        {
            foreach (var definition in SleeveDefinitions)
                allocations[definition.Key] = amount * gaps[definition.Key] / totalGap;
        }
        else
        {
            var leftover = amount - totalGap;
            foreach (var definition in SleeveDefinitions)
                allocations[definition.Key] = gaps[definition.Key] + leftover * targets[definition.Key] / 100m;
        }

        // Rounding to cents must not invent or lose money: the largest allocation absorbs the
        // difference so the parts always add back up to the deposit the user is being told to make.
        var rounded = SleeveDefinitions.ToDictionary(
            definition => definition.Key,
            definition => RoundMoney(allocations[definition.Key]));
        var drift = RoundMoney(amount) - rounded.Values.Sum();
        if (drift != 0)
        {
            var largest = SleeveDefinitions
                .OrderByDescending(definition => rounded[definition.Key])
                .First().Key;
            rounded[largest] += drift;
        }

        var sleeves = SleeveDefinitions.Select(definition =>
        {
            var projectedValue = values[definition.Key] + rounded[definition.Key];
            var projectedPercentage = projectedTotal <= 0 ? 0 : projectedValue / projectedTotal * 100m;
            return new InvestmentContributionSleeveDto(
                definition.Key,
                definition.Label,
                rounded[definition.Key],
                RoundMoney(amount) <= 0 ? 0 : Math.Round(rounded[definition.Key] / RoundMoney(amount) * 100m, 1),
                Math.Round(projectedPercentage, 2),
                Math.Round(projectedPercentage - targets[definition.Key], 2));
        }).ToList();

        var basis = isEstimated
            ? "Uninvested cash in your brokerage accounts."
            : cyclesObserved == 1
                ? "Your Growth deposit from the last completed cycle."
                : $"The median of your Growth deposits across {cyclesObserved} completed cycles.";

        return new InvestmentContributionPlanDto(
            RoundMoney(amount), basis, cyclesObserved, isEstimated, sleeves);
    }

    /// <summary>
    /// The typical Growth deposit per completed cycle, plus how many cycles that was measured
    /// over. The median rather than the mean: a single windfall cycle should not raise what the
    /// user is told to buy every month.
    /// </summary>
    private static (decimal? Amount, int Cycles) UsualCompletedCycleContribution(
        IReadOnlyList<InvestmentContributionDto> contributions,
        int cycleDay)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentCycle = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            today, cycleDay);
        var cycleTotals = contributions
            .Where(value => value.AmountApp > 0)
            .Select(value => new
            {
                Cycle = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
                    value.Date, cycleDay),
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

    private static Dictionary<string, decimal> Targets(InvestmentPlanDto plan) => new()
    {
        ["USEquity"] = plan.UsEquityTarget,
        ["InternationalExUS"] = plan.InternationalExUsTarget,
        ["Bonds"] = plan.BondsTarget
    };

    private static decimal PositiveCash(IReadOnlyList<InvestmentCashBalanceDto> balances)
        => balances.Where(value => value.AmountApp > 0).Sum(value => value.AmountApp ?? 0);

    private static decimal RoundMoney(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Format(decimal amount, string currency)
        => $"{currency} {RoundMoney(amount):N2}";
}

public sealed record InvestmentPlanMutationDto(
    decimal UsEquityTarget,
    decimal InternationalExUsTarget,
    decimal BondsTarget,
    decimal WatchDrift,
    decimal AlertDrift);

public sealed record AllocationSleeveMutationDto(string? Sleeve);
