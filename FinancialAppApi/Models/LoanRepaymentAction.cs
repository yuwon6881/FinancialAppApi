using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public static class LoanRepaymentActionKind
{
    public const string AdvanceCycles = "AdvanceCycles";
    public const string FullSettlement = "FullSettlement";
}

public sealed class LoanRepaymentAction : IUserOwnedEntity
{
    [Key]
    [StringLength(100)]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string LoanId { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string RecurringPaymentId { get; set; } = string.Empty;

    [Required]
    [StringLength(30)]
    public string Kind { get; set; } = LoanRepaymentActionKind.AdvanceCycles;

    public decimal? LenderQuoteAmount { get; set; }

    public DateOnly EffectiveDate { get; set; }

    [Required]
    [StringLength(100)]
    public string ReplayRevision { get; set; } = string.Empty;

    public bool PriorPaymentActive { get; set; }

    [StringLength(20)]
    public string? PriorPaymentEndDate { get; set; }

    /// <summary>
    /// Delimited list of created transaction IDs belonging to this repayment group.
    /// </summary>
    public string TransactionIds { get; set; } = string.Empty;

    /// <summary>
    /// Delimited list of target occurrence dates (yyyy-MM-dd) settled by this action.
    /// </summary>
    public string TargetOccurrenceDates { get; set; } = string.Empty;

    /// <summary>
    /// JSON or fingerprint payload capturing transaction states for undo integrity check.
    /// </summary>
    public string UndoFingerprints { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
