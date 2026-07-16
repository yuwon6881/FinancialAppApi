using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class Transaction
{
    [Key]
    public string Id { get; set; } = string.Empty;

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
}

public class AutocompleteSuggestion
{
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string LedgerCategory { get; set; } = string.Empty;
    public string TxType { get; set; } = string.Empty;
}
