using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

/// <summary>
/// Reversible metadata for the Rewards expense created when a savings commitment is completed.
/// Kept outside Transaction so ordinary ledger rows do not carry a wide set of nullable snapshot
/// columns. TransactionId is also the ledger row id, giving each settlement exactly one snapshot.
/// </summary>
public class SavingsGoalCompletion : IUserOwnedEntity
{
    [Key]
    [StringLength(200)]
    public string TransactionId { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public int SavingsGoalId { get; set; }

    [Required]
    public DateTime PreviousTargetDate { get; set; }

    [Required]
    public decimal PreviousEarmarkedAmount { get; set; }

    [StringLength(7)]
    public string? PreviousCycleFundedKey { get; set; }

    [Required]
    public decimal PreviousCycleFundedAmount { get; set; }

    [Required]
    public DateTime ResultingTargetDate { get; set; }

    [Required]
    public bool WasRecurring { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Set when the linked ledger transaction was deleted and this snapshot restored the goal.
    /// Retaining the row makes a repeated DELETE idempotent after a lost response.
    /// </summary>
    public DateTime? ReversedAt { get; set; }
}
