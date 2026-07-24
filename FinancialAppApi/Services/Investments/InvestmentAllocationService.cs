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
    string? Sleeve);

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
    decimal? MinimumContribution);

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
        bool marketDataConfigured,
        CancellationToken cancellationToken)
    {
        var planEntity = await context.InvestmentPlans.AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        var plan = ToDto(planEntity);
        var assignments = instruments.Select(value => new InvestmentAllocationAssignmentDto(
                value.Id, value.Symbol, value.Name, value.AllocationSleeve))
            .OrderBy(value => value.Symbol)
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

        var minimumNewMoney = SleeveDefinitions.Max(definition =>
            values[definition.Key] / (targets[definition.Key] / 100m) - investedValue);
        minimumNewMoney = Math.Max(0, minimumNewMoney);
        var finalTotal = investedValue + minimumNewMoney;
        var requiredBuys = SleeveDefinitions.ToDictionary(
            definition => definition.Key,
            definition => Math.Max(0, finalTotal * targets[definition.Key] / 100m - values[definition.Key]));
        var recommendations = BuildRecommendations(
            appCurrency, investedValue, values, targets, requiredBuys, availableCash, minimumNewMoney);

        return new InvestmentAllocationOverviewDto(
            status, appCurrency, plan, assignments, sleeves, recommendations, [],
            freshness, RoundMoney(investedValue), RoundMoney(availableCash),
            RoundMoney(Math.Max(0, minimumNewMoney - availableCash)));
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
        IReadOnlyDictionary<string, decimal> requiredBuys,
        decimal cash,
        decimal minimumNewMoney)
    {
        var recommendations = new List<InvestmentAllocationRecommendationDto>();
        var usableCash = Math.Min(cash, minimumNewMoney);
        if (usableCash > 0)
        {
            recommendations.Add(new InvestmentAllocationRecommendationDto(
                1, "UseCash", null, RoundMoney(usableCash),
                $"Invest {Format(usableCash, currency)} of available brokerage cash first."));
        }
        var topUp = Math.Max(0, minimumNewMoney - cash);
        if (topUp > 0)
        {
            recommendations.Add(new InvestmentAllocationRecommendationDto(
                2, "TopUp", null, RoundMoney(topUp),
                $"Add {Format(topUp, currency)} to reach the targets without selling."));
        }
        foreach (var definition in SleeveDefinitions.Where(value => requiredBuys[value.Key] > 0))
        {
            var amount = requiredBuys[definition.Key];
            recommendations.Add(new InvestmentAllocationRecommendationDto(
                3, "Buy", definition.Key, RoundMoney(amount),
                $"Buy {Format(amount, currency)} of {definition.Label} in the new-money plan."));
        }

        foreach (var definition in SleeveDefinitions)
        {
            var difference = values[definition.Key] - investedValue * targets[definition.Key] / 100m;
            if (difference > 0.005m)
                recommendations.Add(new InvestmentAllocationRecommendationDto(
                    4, "Sell", definition.Key, RoundMoney(difference),
                    $"Exact transfer: sell {Format(difference, currency)} of {definition.Label}."));
        }
        foreach (var definition in SleeveDefinitions)
        {
            var difference = investedValue * targets[definition.Key] / 100m - values[definition.Key];
            if (difference > 0.005m)
                recommendations.Add(new InvestmentAllocationRecommendationDto(
                    5, "TransferBuy", definition.Key, RoundMoney(difference),
                    $"Exact transfer: buy {Format(difference, currency)} of {definition.Label}."));
        }
        return recommendations;
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
