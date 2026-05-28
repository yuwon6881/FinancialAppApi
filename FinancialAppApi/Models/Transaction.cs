using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class Transaction
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string Date { get; set; } = string.Empty;

    [Required]
    public string Description { get; set; } = string.Empty;

    [Required]
    public string Category { get; set; } = string.Empty;

    [Required]
    public string LedgerCategory { get; set; } = string.Empty;

    [Required]
    public decimal Amount { get; set; }
}
