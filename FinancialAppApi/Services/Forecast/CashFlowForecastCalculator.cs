using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Services.Forecast;

public static class ForecastBuckets
{
    public const string Essentials = "Essentials";
    public const string Growth = "Growth";
    public const string Stability = "Stability";
    public const string Rewards = "Rewards";

    public static readonly IReadOnlyList<string> All = [Essentials, Growth, Stability, Rewards];

    public static string? Normalize(string? bucket) =>
        All.FirstOrDefault(value => string.Equals(value, bucket, StringComparison.OrdinalIgnoreCase));
}

public static class ForecastItemKinds
{
    /// <summary>A scheduled recurring bill or loan instalment taken from the user's records.</summary>
    public const string Bill = "Bill";
    /// <summary>A temporary what-if line the user added to the scenario.</summary>
    public const string Scenario = "Scenario";
}

/// <summary>
/// One dated movement inside the forecast. A positive amount adds money to its bucket and a
/// negative amount removes it. <see cref="Bucket"/> null means "income": it is divided across the
/// buckets by the same rules a real salary uses, including the Stability target cap.
/// </summary>
public sealed record ForecastItem(
    DateOnly Date,
    decimal Amount,
    string? Bucket,
    string Label,
    string Kind,
    string? SourceId = null);

public sealed record ForecastAllocations(decimal Essentials, decimal Growth, decimal Stability, decimal Rewards);

public sealed record ForecastPeriod(DateOnly Start, DateOnly End, string Label, bool IsCurrent);

public sealed record CashFlowForecastInput(
    IReadOnlyList<ForecastPeriod> Periods,
    IReadOnlyDictionary<string, decimal> OpeningBalances,
    ForecastAllocations Allocations,
    decimal StabilityTarget,
    string? StabilityOverflowRedirect,
    // Income expected at the start of every future cycle. Null means unknown, never zero.
    decimal? IncomePerCycle,
    // Everyday Essentials spending per cycle beyond scheduled bills. Null means not included.
    decimal? EverydaySpendingPerCycle,
    IReadOnlyList<ForecastItem> Items);

public sealed record ForecastLine(DateOnly Date, string Label, string? Bucket, decimal Amount, string Kind, string? SourceId);

public sealed record ForecastCycle(
    DateOnly Start,
    DateOnly End,
    string Label,
    bool IsCurrent,
    IReadOnlyDictionary<string, decimal> Opening,
    IReadOnlyDictionary<string, decimal> MoneyIn,
    IReadOnlyDictionary<string, decimal> MoneyOut,
    IReadOnlyDictionary<string, decimal> Closing,
    IReadOnlyList<ForecastLine> Lines,
    IReadOnlyList<string> ShortBuckets,
    bool StabilityTargetReached);

public sealed record CashFlowForecastResult(
    IReadOnlyList<ForecastCycle> Cycles,
    bool IncomeKnown,
    bool EverydaySpendingIncluded,
    DateOnly? StabilityTargetReachedOn);

/// <summary>
/// Projects bucket balances forward cycle by cycle. Pure and clock-free: the caller supplies the
/// periods, the opening balances and every dated movement, so a scenario is just a different input.
/// <para>
/// Within a cycle, income lands on the cycle's first day (the cycle day is payday), then the
/// everyday spending allowance, then each dated item in date order. Income is split with
/// <see cref="IncomeSplitPlanner"/> against the Stability balance at that moment, so the cycle in
/// which the fund reaches its target automatically redirects the surplus the way the ledger would.
/// </para>
/// The current cycle receives no income: its salary is already in the opening balances.
/// </summary>
public static class CashFlowForecastCalculator
{
    public static CashFlowForecastResult Project(CashFlowForecastInput input)
    {
        var balances = ForecastBuckets.All.ToDictionary(
            bucket => bucket,
            bucket => input.OpeningBalances.GetValueOrDefault(bucket));
        var cycles = new List<ForecastCycle>(input.Periods.Count);
        DateOnly? reachedOn = null;
        var targetSet = input.StabilityTarget > 0m;
        var reachedBeforeStart = targetSet && balances[ForecastBuckets.Stability] >= input.StabilityTarget;

        foreach (var period in input.Periods)
        {
            var opening = Snapshot(balances);
            var moneyIn = Zero();
            var moneyOut = Zero();
            var lines = new List<ForecastLine>();

            void Apply(DateOnly date, string label, string bucket, decimal amount, string kind, string? sourceId)
            {
                balances[bucket] += amount;
                if (amount >= 0m) moneyIn[bucket] += amount; else moneyOut[bucket] += -amount;
                lines.Add(new ForecastLine(date, label, bucket, amount, kind, sourceId));
                if (targetSet && !reachedBeforeStart && reachedOn is null
                    && balances[ForecastBuckets.Stability] >= input.StabilityTarget)
                {
                    reachedOn = date;
                }
            }

            void ApplyIncome(DateOnly date, string label, decimal amount, string kind, string? sourceId)
            {
                var split = IncomeSplitPlanner.Resolve(
                    amount,
                    input.Allocations.Essentials,
                    input.Allocations.Growth,
                    input.Allocations.Stability,
                    input.Allocations.Rewards,
                    balances[ForecastBuckets.Stability],
                    input.StabilityTarget,
                    input.StabilityOverflowRedirect,
                    0m);
                var shares = new[]
                {
                    (ForecastBuckets.Essentials, split.Essentials),
                    (ForecastBuckets.Growth, split.Growth),
                    (ForecastBuckets.Stability, split.Stability),
                    (ForecastBuckets.Rewards, split.Rewards),
                };
                var parts = shares.Select(share => Round(amount * share.Item2)).ToArray();
                // Rounding must not create or lose money: the largest share absorbs the cent drift.
                var drift = Round(amount) - parts.Sum();
                if (drift != 0m)
                {
                    var largest = Array.IndexOf(parts, parts.Max());
                    parts[largest] += drift;
                }
                for (var index = 0; index < shares.Length; index++)
                {
                    if (parts[index] != 0m) Apply(date, label, shares[index].Item1, parts[index], kind, sourceId);
                }
            }

            if (!period.IsCurrent && input.IncomePerCycle is > 0m)
            {
                ApplyIncome(period.Start, "Income", input.IncomePerCycle.Value, ForecastItemKinds.Scenario, null);
            }

            if (!period.IsCurrent && input.EverydaySpendingPerCycle is > 0m)
            {
                Apply(period.Start, "Everyday spending", ForecastBuckets.Essentials,
                    -input.EverydaySpendingPerCycle.Value, ForecastItemKinds.Scenario, null);
            }

            foreach (var item in input.Items
                         .Where(item => item.Date >= period.Start && item.Date <= period.End)
                         .OrderBy(item => item.Date)
                         .ThenBy(item => item.Kind, StringComparer.Ordinal)
                         .ThenBy(item => item.Label, StringComparer.Ordinal))
            {
                var bucket = ForecastBuckets.Normalize(item.Bucket);
                if (bucket is null)
                {
                    if (item.Amount > 0m) ApplyIncome(item.Date, item.Label, item.Amount, item.Kind, item.SourceId);
                    continue;
                }
                Apply(item.Date, item.Label, bucket, item.Amount, item.Kind, item.SourceId);
            }

            var closing = Snapshot(balances);
            cycles.Add(new ForecastCycle(
                period.Start,
                period.End,
                period.Label,
                period.IsCurrent,
                opening,
                moneyIn,
                moneyOut,
                closing,
                lines,
                ForecastBuckets.All.Where(bucket => closing[bucket] < 0m && closing[bucket] < opening[bucket]).ToList(),
                targetSet && closing[ForecastBuckets.Stability] >= input.StabilityTarget));
        }

        return new CashFlowForecastResult(
            cycles,
            input.IncomePerCycle.HasValue,
            input.EverydaySpendingPerCycle.HasValue,
            reachedOn);
    }

    private static Dictionary<string, decimal> Zero() => ForecastBuckets.All.ToDictionary(bucket => bucket, _ => 0m);

    private static Dictionary<string, decimal> Snapshot(Dictionary<string, decimal> balances) =>
        ForecastBuckets.All.ToDictionary(bucket => bucket, bucket => Round(balances[bucket]));

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
