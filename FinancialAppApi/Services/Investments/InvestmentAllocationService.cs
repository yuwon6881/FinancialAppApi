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
    decimal? RoutineContribution,
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
    decimal? AvailableCash,
    decimal? MinimumContribution,
    InvestmentContributionPlanDto? ContributionPlan = null);

public sealed partial class InvestmentAllocationService(AppDbContext context)
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
        var availableCash = PositiveCash(cashBalances);
        var cashReasons = cashBalances
            .Where(value => value.Amount > 0 && value.AmountApp is null)
            .Select(value => $"{value.Currency} cash in {value.AccountName} cannot be valued in {appCurrency}.")
            .Distinct()
            .ToList();
        var cycleDay = await context.FinancialSettings.AsNoTracking()
            .Select(value => (int?)value.CycleDay)
            .SingleOrDefaultAsync(cancellationToken) ?? 28;
        var (usualGrowthDeposit, cyclesObserved) = UsualCompletedCycleContribution(contributions, cycleDay);

        if (holdings.Count == 0)
        {
            var emptyValues = SleeveDefinitions.ToDictionary(value => value.Key, _ => 0m);
            return new InvestmentAllocationOverviewDto(
                "NotStarted", appCurrency, plan, assignments,
                EmptySleeves(plan), [], cashReasons, freshness, 0, availableCash,
                availableCash is null ? null : 0,
                availableCash is null ? null : BuildContributionPlan(
                    appCurrency, 0, emptyValues, Targets(plan), 0, false,
                    usualGrowthDeposit, cyclesObserved, availableCash.Value));
        }

        var incompleteReasons = reasons.Concat(cashReasons).Distinct().ToList();
        var holdingReasons = reasons.Distinct().ToList();
        if (holdingReasons.Count > 0)
        {
            return new InvestmentAllocationOverviewDto(
                "Incomplete", appCurrency, plan, assignments,
                IncompleteSleeves(plan, holdings, instruments), [], incompleteReasons,
                freshness, null, availableCash, null);
        }

        var investedValue = holdings.Sum(value => value.ValueApp!.Value);
        if (investedValue <= 0)
        {
            return new InvestmentAllocationOverviewDto(
                "NotStarted", appCurrency, plan, assignments,
                EmptySleeves(plan), [], cashReasons, freshness, investedValue, availableCash,
                availableCash is null ? null : 0);
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
        var minimumNewMoney = SleeveDefinitions.Max(definition =>
            values[definition.Key] / (targets[definition.Key] / 100m) - investedValue);
        minimumNewMoney = Math.Max(0, minimumNewMoney);

        var recommendations = status == "OnTrack" || availableCash is null
            ? new List<InvestmentAllocationRecommendationDto>()
            : BuildRecommendations(
                appCurrency, investedValue, values, targets, availableCash.Value, usualGrowthDeposit,
                plan.WatchDrift);

        return new InvestmentAllocationOverviewDto(
            status, appCurrency, plan, assignments, sleeves, recommendations, incompleteReasons,
            freshness, RoundMoney(investedValue), availableCash is null ? null : RoundMoney(availableCash.Value),
            availableCash is null ? null : RoundMoney(Math.Max(0, minimumNewMoney - availableCash.Value - (usualGrowthDeposit ?? 0))),
            availableCash is null ? null : BuildContributionPlan(
                appCurrency, investedValue, values, targets, minimumNewMoney, status != "OnTrack",
                usualGrowthDeposit, cyclesObserved, availableCash.Value));
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

    public async Task<InvestmentPlanDto> UpdatePlanAsync(
        InvestmentPlanMutationDto value,
        CancellationToken cancellationToken)
    {
        var plan = await context.InvestmentPlans.SingleOrDefaultAsync(cancellationToken);
        if (plan is null)
        {
            plan = new InvestmentPlan();
            context.InvestmentPlans.Add(plan);
        }

        plan.UsEquityTarget = value.UsEquityTarget;
        plan.InternationalExUsTarget = value.InternationalExUsTarget;
        plan.BondsTarget = value.BondsTarget;
        plan.WatchDrift = value.WatchDrift;
        plan.AlertDrift = value.AlertDrift;
        plan.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return ToDto(plan);
    }

    public async Task<bool> UpdateAllocationSleeveAsync(
        Guid instrumentId,
        string? sleeve,
        CancellationToken cancellationToken)
    {
        var instrument = await context.InvestmentInstruments
            .SingleOrDefaultAsync(value => value.Id == instrumentId, cancellationToken);
        if (instrument is null) return false;

        instrument.AllocationSleeve = sleeve is null
            ? null
            : InvestmentKinds.AllocationSleeves.Single(value =>
                value.Equals(sleeve, StringComparison.OrdinalIgnoreCase));
        instrument.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateAllocationOrderAsync(
        IReadOnlyList<Guid> instrumentIds,
        CancellationToken cancellationToken)
    {
        var instruments = await context.InvestmentInstruments
            .ToDictionaryAsync(value => value.Id, cancellationToken);
        if (instruments.Count != instrumentIds.Count || instrumentIds.Any(id => !instruments.ContainsKey(id)))
            return false;

        for (var index = 0; index < instrumentIds.Count; index++)
        {
            var instrument = instruments[instrumentIds[index]];
            instrument.AllocationOrder = index;
            instrument.UpdatedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken);
        return true;
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
        decimal availableCash)
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

    private static Dictionary<string, decimal> Targets(InvestmentPlanDto plan) => new()
    {
        ["USEquity"] = plan.UsEquityTarget,
        ["InternationalExUS"] = plan.InternationalExUsTarget,
        ["Bonds"] = plan.BondsTarget
    };

    private static decimal? PositiveCash(IReadOnlyList<InvestmentCashBalanceDto> balances)
        => balances.Any(value => value.Amount > 0 && value.AmountApp is null)
            ? null
            : balances.Where(value => value.AmountApp > 0).Sum(value => value.AmountApp ?? 0);

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
