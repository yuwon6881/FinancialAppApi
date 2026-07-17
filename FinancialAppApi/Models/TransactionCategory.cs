using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class TransactionCategory : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;
}
