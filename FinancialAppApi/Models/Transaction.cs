using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class Transaction
{
    [Key]
    public string Id { get; set; } = string.Empty;

    public DateOnly Date { get; set; }

    [Required]
    public string Description { get; set; } = string.Empty;

    [Required]
    public string Category { get; set; } = string.Empty;

    [Required]
    public string LedgerCategory { get; set; } = string.Empty;

    [Required]
    public decimal Amount { get; set; }

    // Denormalized reference to the RecurringPayment this transaction was generated from, if any.
    // Intentionally not a real FK: it must survive the parent RecurringPayment being deleted so
    // historical cycles can still be identified as subscription payments.
    public string? RecurringPaymentId { get; set; }
}

public class AutocompleteSuggestion
{
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string LedgerCategory { get; set; } = string.Empty;
    public string TxType { get; set; } = string.Empty;
}
