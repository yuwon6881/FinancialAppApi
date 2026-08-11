namespace FinancialAppApi.Models;

/// <summary>
/// The Stability target and normal income allocation that were in force at one point in time.
/// Reload replay uses these revisions instead of applying today's settings to old transactions.
/// </summary>
public sealed class StabilityPlanRevision : IUserOwnedEntity
{
    public long Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public DateTime EffectiveAt { get; set; }

    public decimal TargetStabilityFund { get; set; }

    public decimal StabilityAlloc { get; set; }
}
