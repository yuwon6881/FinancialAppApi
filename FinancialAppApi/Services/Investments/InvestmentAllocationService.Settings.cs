using System.Text.Json;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

public sealed record InvestmentGroup(string Key, string Label, decimal TargetPercentage, string? ForecastClass);

public sealed partial class InvestmentAllocationService
{
    internal static string? ValidatePlan(InvestmentPlanMutationDto value, IReadOnlyList<InvestmentGroup> groups)
    {
        if (groups.Count > 20)
            return "Use at most 20 portfolio groups.";
        if (groups.Any(group => group is null || string.IsNullOrWhiteSpace(group.Label) || group.Label.Trim().Length > 60
            || group.Key is not { Length: >= 1 and <= 32 } || !group.Key.All(char.IsAsciiLetterOrDigit)
            || (group.ForecastClass is not null && !InvestmentKinds.ForecastClasses.Contains(group.ForecastClass))))
            return "Each group needs a valid name, identifier and forecast assumption.";
        if (groups.Select(group => group.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Count
            || groups.Select(group => group.Label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Count)
            return "Each group must have a different name and identifier.";
        if (groups.Any(group => group.TargetPercentage is <= 0 or > 100 || decimal.Round(group.TargetPercentage, 2) != group.TargetPercentage)
            || (groups.Count > 0 && groups.Sum(group => group.TargetPercentage) != 100))
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
        await using var lease = await LockPlanAsync(cancellationToken);
        var plan = await context.InvestmentPlans.SingleOrDefaultAsync(cancellationToken);
        var previous = ToDto(plan);
        var requested = value.Groups ?? ApplyLegacyTargets(value, previous.Groups);
        if (requested is null)
            return (null, "Reload the app to edit this investment plan.");
        var error = ValidatePlan(value, requested);
        if (error is not null) return (null, error);
        var existingKeys = previous.Groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        if (requested.Any(group => !existingKeys.Contains(group.Key) && !Guid.TryParseExact(group.Key, "N", out _)))
            return (null, "Reload the app to add a new portfolio group.");

        var groups = requested
            .Select(group => group with
            {
                Key = Guid.TryParseExact(group.Key, "N", out var id) ? id.ToString("N") : group.Key,
                Label = group.Label.Trim(),
                ForecastClass = InvestmentKinds.ForecastClasses.FirstOrDefault(item =>
                    item.Equals(group.ForecastClass, StringComparison.OrdinalIgnoreCase))
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

        plan.GroupsJson = JsonSerializer.Serialize(groups);
        plan.GroupingEnabled = value.GroupingEnabled ?? previous.GroupingEnabled;
        plan.TargetsEnabled = value.TargetsEnabled ?? previous.TargetsEnabled;
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
            if (plan?.GroupingEnabled != true) return (true, false);
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
        var groups = value is not null && value.GroupsJson is { } json
            ? JsonSerializer.Deserialize<InvestmentGroup[]>(json) ?? throw new InvalidOperationException("Missing investment groups.")
            : [];
        return new InvestmentPlanDto(value?.Id, value?.WatchDrift ?? 3, value?.AlertDrift ?? 5,
            value?.UpdatedAt, groups, value?.GroupingEnabled ?? false, value?.TargetsEnabled ?? false);
    }

    // An older client can replay a queued percentage edit against the same migrated groups.
    private static InvestmentGroup[]? ApplyLegacyTargets(InvestmentPlanMutationDto value, IReadOnlyList<InvestmentGroup> groups)
    {
        if (value.UsEquityTarget is not { } us || value.InternationalExUsTarget is not { } international
            || value.BondsTarget is not { } bonds) return null;
        var targets = new Dictionary<string, decimal> { ["USEquity"] = us, ["InternationalExUS"] = international, ["Bonds"] = bonds };
        return groups.Count == targets.Count && groups.All(group => targets.ContainsKey(group.Key))
            ? groups.Select(group => group with { TargetPercentage = targets[group.Key] }).ToArray()
            : null;
    }

    private Task<IAsyncDisposable> LockPlanAsync(CancellationToken cancellationToken) =>
        PostgresAdvisoryLock.AcquireAsync(context, $"financial-app:investment-plan:{context.CurrentUserId}", cancellationToken);
}
