using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public sealed class RecurringPaymentOccurrence : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    // Deliberately not a foreign key. Occurrence history survives deletion of its schedule.
    [Required]
    public string RecurringPaymentId { get; set; } = string.Empty;

    public DateOnly OccurrenceDate { get; set; }

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    public decimal? ScheduledAmount { get; set; }

    [StringLength(100)]
    public string? Category { get; set; }

    [StringLength(200)]
    public string? LedgerCategory { get; set; }

    // The account this occurrence was scheduled to be paid from, frozen with the rest of the
    // snapshot. Settlement reads this rather than the parent's current AccountId so re-pointing a
    // bill moves only its future occurrences; one already materialised still settles where it was
    // scheduled. Null on legacy rows materialised before the account cutover, where the caller
    // falls back to the parent.
    [StringLength(100)]
    public string? AccountId { get; set; }

    [Required]
    [StringLength(20)]
    public string PaymentMode { get; set; } = RecurringPaymentMode.Manual;

    [Required]
    [StringLength(20)]
    public string Status { get; set; } = RecurringOccurrenceStatus.Pending;

    public DateOnly? PaidDate { get; set; }

    // Deliberately denormalized so deleting/restoring a transaction can reopen/resettle atomically.
    public string? SettlementTransactionId { get; set; }
}

public static class RecurringOccurrenceStatus
{
    public const string Pending = "Pending";
    public const string Paid = "Paid";
    public const string Discarded = "Discarded";
}
