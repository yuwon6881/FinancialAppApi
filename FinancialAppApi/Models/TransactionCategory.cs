using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class TransactionCategory
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;
}
