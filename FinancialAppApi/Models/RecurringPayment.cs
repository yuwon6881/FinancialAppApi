using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class RecurringPayment
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public decimal Amount { get; set; }

    [Required]
    public string Frequency { get; set; } = string.Empty; // "Weekly" | "Monthly" | "Annually"

    [Required]
    [StringLength(100)]
    public string Category { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string LedgerCategory { get; set; } = string.Empty;

    [Required]
    public string NextDueDate { get; set; } = string.Empty;

    [Required]
    public int DueDate { get; set; } // Day of the month (e.g. 14)

    [Required]
    public string StartDate { get; set; } = string.Empty; // Start date (yyyy-MM-dd)

    [Required]
    public bool Active { get; set; }

    public string? EndDate { get; set; } // End date (yyyy-MM-dd, optional)
}
