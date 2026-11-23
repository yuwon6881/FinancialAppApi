using System.Text.Json;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

public sealed record InvestmentGroup(string Key, string Label, decimal TargetPercentage, string? ForecastClass);

public sealed partial class InvestmentAllocationService
{
    private static InvestmentGroup[] DefaultGroups(decimal us, decimal international, decimal bonds) =>
    [
        new("USEquity", "US Equity", us, "USEquity"),
        new("InternationalExUS", "International ex-US", international, "InternationalExUS"),
        new("Bonds", "Bonds", bonds, "Bonds")
    ];

    public static string? ValidatePlan(InvestmentPlanMutationDto value)
    {
        var groups = value.Groups ?? DefaultGroups(value.UsEquityTarget, value.InternationalExUsTarget, value.BondsTarget);
        if (groups.Count is < 1 or > 20)
            return "Use between 1 and 20 portfolio groups.";
        if (groups.Any(group => group is null || string.IsNullOrWhiteSpace(group.Label) || group.Label.Trim().Length > 60
            || (!InvestmentKinds.AllocationSleeves.Contains(group.Key ?? "") && !Guid.TryParseExact(group.Key, "N", out _))
            || (group.ForecastClass is not null && !InvestmentKinds.AllocationSleeves.Contains(group.ForecastClass))))
            return "Each group needs a valid name, identifier and forecast assumption.";
        if (groups.Select(group => group.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Count
            || groups.Select(group => group.Label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Count)
            return "Each group must have a different name and identifier.";
        if (groups.Any(group => group.TargetPercentage is <= 0 or > 100 || decimal.Round(group.TargetPercentage, 2) != group.TargetPercentage)
            || groups.Sum(group => group.TargetPercentage) != 100)
            return "Group targets must be positive and total exactly 100%.";
        if (value.WatchDrift <= 0 || value.AlertDrift <= value.WatchDrift || value.AlertDrift > 100
            || decimal.Round(value.WatchDrift, 2) != value.WatchDrift || decimal.Round(value.AlertDrift, 2) != value.AlertDrift)
            return "The off-target threshold must be higher than the watch threshold and at most 100 percentage points.";
        return null;
    }

    public async Task<(InvestmentPlanDto? Plan, string? Error)> UpdatePlanAsync(
        InvestmentPlanMutationDto value,
        CancellationToken cancellationToken)
    {
        var error = ValidatePlan(value);
        if (error is not null) return (null, error);

        await using var lease = await LockPlanAsync(cancellationToken);
        var plan = await context.InvestmentPlans.SingleOrDefaultAsync(cancellationToken);
        if (value.Groups is null && plan?.GroupsJson is not null)
            return (null, "Reload the app to edit these portfolio groups.");

        var groups = (value.Groups ?? DefaultGroups(value.UsEquityTarget, value.InternationalExUsTarget, value.BondsTarget))
            .Select(group => group with
            {
                Key = CanonicalGroupKey(group.Key),
                Label = group.Label.Trim(),
                ForecastClass = group.ForecastClass is null ? null : CanonicalGroupKey(group.ForecastClass)
            }).ToArray();
        var keys = groups.Select(group => group.Key).ToArray();
        if (await context.InvestmentInstruments.AnyAsync(instrument =>
            instrument.AllocationSleeve != null && !keys.Contains(instrument.AllocationSleeve), cancellationToken))
            return (null, "Move investments to another portfolio group before removing their group.");

        if (plan is null)
        {
            plan = new InvestmentPlan();
            context.InvestmentPlans.Add(plan);
        }

        // Keep the original columns readable by older clients and retain the legacy mutation path
        // while the plan uses only the original groups with their original names and assumptions.
        var standard = DefaultGroups(0, 0, 0);
        var isDefaultShape = groups.Length == standard.Length && groups.Zip(standard).All(pair =>
            pair.First with { TargetPercentage = 0 } == pair.Second);
        if (isDefaultShape)
        {
            plan.UsEquityTarget = groups[0].TargetPercentage;
            plan.InternationalExUsTarget = groups[1].TargetPercentage;
            plan.BondsTarget = groups[2].TargetPercentage;
        }
        plan.GroupsJson = isDefaultShape ? null : JsonSerializer.Serialize(groups);
        plan.WatchDrift = value.WatchDrift;
        plan.AlertDrift = value.AlertDrift;
        plan.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return (ToDto(plan), null);
    }

    public async Task<(bool Found, bool ValidGroup)> UpdateAllocationSleeveAsync(
        Guid instrumentId,
        string? sleeve,
        CancellationToken cancellationToken)
    {
        await using var lease = await LockPlanAsync(cancellationToken);
        var instrument = await context.InvestmentInstruments
            .SingleOrDefaultAsync(value => value.Id == instrumentId, cancellationToken);
        if (instrument is null) return (false, true);

        InvestmentGroup? group = null;
        if (sleeve is not null)
        {
            var plan = await context.InvestmentPlans.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            group = ToDto(plan).Groups.FirstOrDefault(value => value.Key.Equals(sleeve, StringComparison.OrdinalIgnoreCase));
            if (group is null) return (true, false);
        }

        instrument.AllocationSleeve = group?.Key;
        instrument.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return (true, true);
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

    public static InvestmentPlanDto ToDto(InvestmentPlan? value)
    {
        var us = value?.UsEquityTarget ?? 66;
        var international = value?.InternationalExUsTarget ?? 10;
        var bonds = value?.BondsTarget ?? 24;
        var groups = value?.GroupsJson is { } json
            ? JsonSerializer.Deserialize<InvestmentGroup[]>(json) ?? throw new InvalidOperationException("Missing investment groups.")
            : DefaultGroups(us, international, bonds);
        return new InvestmentPlanDto(value?.Id, us, international, bonds,
            value?.WatchDrift ?? 3, value?.AlertDrift ?? 5, value?.UpdatedAt, groups);
    }

    private static string CanonicalGroupKey(string key) => InvestmentKinds.AllocationSleeves
        .FirstOrDefault(standard => standard.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key.ToLowerInvariant();

    private Task<IAsyncDisposable> LockPlanAsync(CancellationToken cancellationToken) =>
        PostgresAdvisoryLock.AcquireAsync(context, $"financial-app:investment-plan:{context.CurrentUserId}", cancellationToken);
}
