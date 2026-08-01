using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class Transaction : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    public DateTime PostedAt { get; set; } = DateTime.UtcNow;

    [Required]
    public string Description { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string Category { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string LedgerCategory { get; set; } = string.Empty;

    [Required]
    public decimal Amount { get; set; }

    // Denormalized reference to the RecurringPayment this transaction was generated from, if any.
    // Intentionally not a real FK: it must survive the parent RecurringPayment being deleted so
    // historical cycles can still be identified as subscription payments.
    public string? RecurringPaymentId { get; set; }

    // Denormalized reference to the WishlistItem this transaction purchased, if any.
    // Intentionally not a real FK: the transaction can be deleted independently, and the
    // delete path clears the wishlist purchase state instead of blocking the ledger delete.
    public int? WishlistItemId { get; set; }

    // Denormalized reference to the SavingsGoal completion this transaction settled, if any.
    // The accompanying SavingsGoalCompletion row holds the reversible pre-completion snapshot.
    // This stays nullable for ordinary ledger rows and is intentionally not a foreign key: deleting
    // the ledger row is the supported way to undo the completion.
    public int? SavingsGoalId { get; set; }

    // The exact recurrence-engine billing date (calendar day, no time) this transaction settles,
    // when it was recorded through a recurring-payment-aware path (normal confirmation or
    // pay-early). Null for legacy rows and for transactions that aren't tied to a matching
    // occurrence. A PostgreSQL partial unique index on (UserId, RecurringPaymentId,
    // RecurringOccurrenceDate) — non-null only — guarantees a given occurrence can never be
    // paid twice.
    public DateOnly? RecurringOccurrenceDate { get; set; }
}

public class AutocompleteSuggestion
{
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string LedgerCategory { get; set; } = string.Empty;
    public string TxType { get; set; } = string.Empty;
}
