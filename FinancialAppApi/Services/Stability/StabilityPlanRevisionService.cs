using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Stability;

public sealed record StabilityPlanSnapshot(
    DateTime EffectiveAt,
    decimal TargetStabilityFund,
    decimal StabilityAlloc);

/// <summary>
/// Persists the target and normal Stability share as an effective-dated timeline. A replay must
/// use the plan that was in force when a movement happened; applying today's settings to old rows
/// can resurrect an obligation that was already cleared or turn an ordinary salary into repayment.
/// </summary>
public sealed class StabilityPlanRevisionService
{
    private static readonly DateTime Baseline = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _context;

    public StabilityPlanRevisionService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<StabilityPlanSnapshot>> GetAsync(
        FinancialSetting setting,
        CancellationToken cancellationToken = default)
    {
        var revisions = await _context.StabilityPlanRevisions
            .AsNoTracking()
            .OrderBy(revision => revision.EffectiveAt)
            .ThenBy(revision => revision.Id)
            .Select(revision => new StabilityPlanSnapshot(
                revision.EffectiveAt,
                revision.TargetStabilityFund,
                revision.StabilityAlloc))
            .ToListAsync(cancellationToken);

        if (revisions.Count > 0) return revisions;

        // In-memory tests and a database created between the settings row and this migration can
        // legitimately have no history yet. The migration seeds existing users; this fallback
        // keeps first use deterministic until the baseline is written.
        return [new StabilityPlanSnapshot(
            Baseline,
            setting.TargetStabilityFund,
            setting.StabilityAlloc)];
    }

    public async Task EnsureBaselineAsync(
        FinancialSetting setting,
        CancellationToken cancellationToken = default)
    {
        if (await _context.StabilityPlanRevisions.AnyAsync(cancellationToken)) return;

        _context.StabilityPlanRevisions.Add(new StabilityPlanRevision
        {
            EffectiveAt = Baseline,
            TargetStabilityFund = setting.TargetStabilityFund,
            StabilityAlloc = setting.StabilityAlloc,
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task AppendIfChangedAsync(
        FinancialSetting previous,
        FinancialSetting next,
        DateTime effectiveAt,
        CancellationToken cancellationToken = default)
    {
        var targetChanged = previous.TargetStabilityFund != next.TargetStabilityFund;
        var allocationChanged = previous.StabilityAlloc != next.StabilityAlloc;
        if (!targetChanged && !allocationChanged) return;

        if (!await _context.StabilityPlanRevisions.AnyAsync(cancellationToken))
        {
            _context.StabilityPlanRevisions.Add(new StabilityPlanRevision
            {
                EffectiveAt = Baseline,
                TargetStabilityFund = previous.TargetStabilityFund,
                StabilityAlloc = previous.StabilityAlloc,
            });
        }

        _context.StabilityPlanRevisions.Add(new StabilityPlanRevision
        {
            EffectiveAt = DateTime.SpecifyKind(effectiveAt, DateTimeKind.Utc),
            TargetStabilityFund = next.TargetStabilityFund,
            StabilityAlloc = next.StabilityAlloc,
        });
    }

    public static StabilityPlanSnapshot At(
        IReadOnlyList<StabilityPlanSnapshot> revisions,
        DateTime timestamp)
    {
        if (revisions.Count == 0)
        {
            return new StabilityPlanSnapshot(Baseline, 0m, 0m);
        }

        var normalized = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
        StabilityPlanSnapshot? selected = null;
        foreach (var revision in revisions)
        {
            if (revision.EffectiveAt > normalized) break;
            selected = revision;
        }
        return selected ?? revisions[0];
    }
}
