using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

/// <summary>A stale-safe, idempotent rollback boundary for one cycle-funding waterfall.</summary>
public sealed class SavingsGoalFundingAction : IUserOwnedEntity
{
    [Key, StringLength(64)]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required, StringLength(20)]
    public string FundingBucket { get; set; } = string.Empty;

    [Required]
    public string PreviousStateJson { get; set; } = "[]";

    [Required]
    public string ResultingStateJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReversedAt { get; set; }
}
