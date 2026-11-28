using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

public sealed record InvestmentPlanDto(
    Guid? Id,
    decimal WatchDrift,
    decimal AlertDrift,
    DateTime? UpdatedAt,
    IReadOnlyList<InvestmentGroup> Groups,
    bool GroupingEnabled,
    bool TargetsEnabled);

public sealed record InvestmentAllocationAssignmentDto(
    Guid InstrumentId,
    string Symbol,
    string Name,
    string? Sleeve,
    int Order);

public sealed record InvestmentSleeveAllocationDto(
    string Sleeve,
    string Label,
    decimal? TargetPercentage,
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
/// How to split the next routine Growth deposit across the portfolio groups so the
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
        var targets = Targets(plan);
        var labels = plan.Groups.ToDictionary(group => group.Key, group => group.Label);
        // Archived investments hold no units and cannot be bought, so they take no part in
        // classification, group counts, or the notices a group removal raises.
        var assignments = instruments.Where(value => !value.IsArchived).Select(value => new InvestmentAllocationAssignmentDto(
                value.Id, value.Symbol, value.Name, value.AllocationSleeve, value.AllocationOrder))
            .OrderBy(value => value.Order)
            .ThenBy(value => value.Symbol)
            .ToList();
        var reasons = new List<string>();
        var freshnessInputs = new List<(string Label, DateTime? FetchedAt)>();

        foreach (var holding in holdings)
        {
            var assignment = instruments.FirstOrDefault(value => value.Id == holding.InstrumentId);
            if (plan.GroupingEnabled && plan.TargetsEnabled && targets.Count > 0
                && (assignment?.AllocationSleeve is not { } key || !targets.ContainsKey(key)))
                reasons.Add($"{holding.Symbol} must be assigned to a portfolio group.");
            if (holding.LatestPriceNative is null)
                reasons.Add($"{holding.Symbol} does not have a current price.");
            if (holding.ValueApp is null)
                reasons.Add($"{holding.Symbol} cannot be valued in {appCurrency}.");

            freshnessInputs.Add(($"{holding.Symbol} price", holding.PriceFetchedAt));
            if (!holding.Currency.Equals(appCurrency, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(holding.FxSource, "Manual FX", StringComparison.OrdinalIgnoreCase))
                freshnessInputs.Add(($"{holding.Currency}/{appCurrency} FX", holding.FxFetchedAt));
        }
        foreach (var cash in cashBalances.Where(value => value.Amount != 0 && value.AmountApp is null))
            freshnessInputs.Add(($"{cash.Currency}/{appCurrency} FX", null));

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
        var investedValue = holdings.Any(value => value.ValueApp is null)
            ? (decimal?)null : holdings.Sum(value => value.ValueApp!.Value);
        var roundedCash = availableCash is { } positiveCash ? RoundMoney(positiveCash) : (decimal?)null;
        if (!plan.GroupingEnabled || targets.Count == 0)
        {
            return new InvestmentAllocationOverviewDto(
                "NotStarted", appCurrency, plan, assignments, [], [], [], freshness,
                investedValue is { } total ? RoundMoney(total) : null, roundedCash, null);
        }
        if (!plan.TargetsEnabled)
            return BuildGroupedOverview(appCurrency, plan, assignments, holdings, freshness, investedValue, roundedCash);

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
            var emptyValues = targets.Keys.ToDictionary(key => key, _ => 0m);
            return new InvestmentAllocationOverviewDto(
                "NotStarted", appCurrency, plan, assignments,
                EmptySleeves(plan), [], cashReasons, freshness, 0, roundedCash,
                availableCash is null ? null : 0,
                availableCash is null ? null : BuildContributionPlan(
                    appCurrency, 0, emptyValues, targets, 0, false,
                    usualGrowthDeposit, cyclesObserved, availableCash.Value, labels));
        }

        var incompleteReasons = reasons.Concat(cashReasons).Distinct().ToList();
        var holdingReasons = reasons.Distinct().ToList();
        if (holdingReasons.Count > 0)
        {
            return new InvestmentAllocationOverviewDto(
                "Incomplete", appCurrency, plan, assignments,
                IncompleteSleeves(plan, holdings, instruments.ToDictionary(value => value.Id, value => value.AllocationSleeve))
                    .Select(sleeve => sleeve with { Value = sleeve.Value is { } value ? RoundMoney(value) : null })
                    .ToList(),
                [], incompleteReasons,
                freshness, null, roundedCash, null);
        }

        var totalInvested = holdings.Sum(value => value.ValueApp!.Value);
        if (totalInvested <= 0)
        {
            return new InvestmentAllocationOverviewDto(
                "NotStarted", appCurrency, plan, assignments,
                EmptySleeves(plan), [], cashReasons, freshness, totalInvested, roundedCash,
                availableCash is null ? null : 0);
        }

        var values = plan.Groups.ToDictionary(
            definition => definition.Key,
            definition => holdings.Where(holding =>
                    instruments.First(instrument => instrument.Id == holding.InstrumentId)
                        .AllocationSleeve == definition.Key)
                .Sum(holding => holding.ValueApp!.Value));
        var sleeves = BuildValuedSleeves(plan, totalInvested, values);
        var status = sleeves.Any(value => value.Status == "Alert")
            ? "Alert"
            : sleeves.Any(value => value.Status == "Watch") ? "Watch" : "OnTrack";
        var minimumNewMoney = plan.Groups.Max(definition =>
            values[definition.Key] / (targets[definition.Key] / 100m) - totalInvested);
        minimumNewMoney = Math.Max(0, minimumNewMoney);

        var recommendations = status == "OnTrack" || availableCash is null
            ? new List<InvestmentAllocationRecommendationDto>()
            : BuildRecommendations(
                appCurrency, totalInvested, values, targets, availableCash.Value, usualGrowthDeposit,
                plan.WatchDrift, labels);

        return new InvestmentAllocationOverviewDto(
            status, appCurrency, plan, assignments, sleeves, recommendations, incompleteReasons,
            freshness, RoundMoney(totalInvested), availableCash is null ? null : RoundMoney(availableCash.Value),
            availableCash is null ? null : RoundMoney(Math.Max(0, minimumNewMoney - availableCash.Value - (usualGrowthDeposit ?? 0))),
            availableCash is null ? null : BuildContributionPlan(
                appCurrency, totalInvested, values, targets, minimumNewMoney, status != "OnTrack",
                usualGrowthDeposit, cyclesObserved, availableCash.Value, labels));
    }

    internal static string ClassifyDrift(decimal absoluteDrift, decimal watchDrift, decimal alertDrift)
        => absoluteDrift >= alertDrift ? "Alert" : absoluteDrift >= watchDrift ? "Watch" : "OnTrack";

    private static InvestmentAllocationOverviewDto BuildGroupedOverview(
        string currency, InvestmentPlanDto plan, IReadOnlyList<InvestmentAllocationAssignmentDto> assignments,
        IReadOnlyList<InvestmentHoldingDto> holdings, InvestmentMarketDataFreshnessDto freshness,
        decimal? total, decimal? cash)
    {
        var basketValues = IncompleteSleeves(plan, holdings, assignments.ToDictionary(value => value.InstrumentId, value => value.Sleeve));
        var sleeves = basketValues.Select(sleeve => sleeve with
        {
            TargetPercentage = null,
            CurrentPercentage = sleeve.Value is { } value && total > 0 ? Math.Round(value / total.Value * 100m, 2) : null,
            Value = sleeve.Value is { } amount ? RoundMoney(amount) : null,
            Status = "NotStarted"
        }).ToList();
        return new("NotStarted", currency, plan, assignments, sleeves, [], [], freshness,
            total is { } invested ? RoundMoney(invested) : null, cash, null);
    }

    private static IReadOnlyList<InvestmentSleeveAllocationDto> EmptySleeves(InvestmentPlanDto plan)
        => plan.Groups.Select(value => new InvestmentSleeveAllocationDto(
            value.Key, value.Label, value.TargetPercentage, null, 0, null, null, "NotStarted")).ToList();

    private static IReadOnlyList<InvestmentSleeveAllocationDto> IncompleteSleeves(
        InvestmentPlanDto plan,
        IReadOnlyList<InvestmentHoldingDto> holdings,
        IReadOnlyDictionary<Guid, string?> assignments)
    {
        var targets = Targets(plan);
        return plan.Groups.Select(definition =>
        {
            var members = holdings.Where(holding => assignments.GetValueOrDefault(holding.InstrumentId) == definition.Key).ToList();
            var value = members.Any(holding => holding.ValueApp is null)
                ? null
                : (decimal?)members.Sum(holding => holding.ValueApp ?? 0);
            return new InvestmentSleeveAllocationDto(
                definition.Key, definition.Label, targets[definition.Key], null, value, null, null, "Incomplete");
        }).ToList();
    }

    private static IReadOnlyList<InvestmentSleeveAllocationDto> BuildValuedSleeves(
        InvestmentPlanDto plan,
        decimal total,
        IReadOnlyDictionary<string, decimal> values)
    {
        var targets = Targets(plan);
        return plan.Groups.Select(definition =>
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

    private static Dictionary<string, decimal> Targets(InvestmentPlanDto plan)
        => plan.Groups.ToDictionary(group => group.Key, group => group.TargetPercentage);

    private static decimal? PositiveCash(IReadOnlyList<InvestmentCashBalanceDto> balances)
        => balances.Any(value => value.Amount > 0 && value.AmountApp is null)
            ? null
            : balances.Where(value => value.AmountApp > 0).Sum(value => value.AmountApp ?? 0);

    private static decimal RoundMoney(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Format(decimal amount, string currency)
        => $"{currency} {RoundMoney(amount):N2}";
}

public sealed record InvestmentPlanMutationDto(
    decimal? UsEquityTarget,
    decimal? InternationalExUsTarget,
    decimal? BondsTarget,
    decimal WatchDrift,
    decimal AlertDrift,
    IReadOnlyList<InvestmentGroup>? Groups = null,
    bool? GroupingEnabled = null,
    bool? TargetsEnabled = null,
    IReadOnlyList<InvestmentAssignmentRestoreDto>? RestoreAssignments = null);

/// <summary>An assignment an undo puts back after the save it reverses removed its group.</summary>
public sealed record InvestmentAssignmentRestoreDto(Guid InstrumentId, string? Sleeve);

public sealed record AllocationSleeveMutationDto(string? Sleeve);
