using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class RecurringPaymentDismissal
{
    [Key]
    public string Id { get; set; } = string.Empty; // Format: "{rpId}-{year}-{month}"

    [Required]
    public string RecurringPaymentId { get; set; } = string.Empty;

    [Required]
    public int Year { get; set; }

    [Required]
    public int Month { get; set; }

    [Required]
    public string DismissedAt { get; set; } = string.Empty; // Date string
}
