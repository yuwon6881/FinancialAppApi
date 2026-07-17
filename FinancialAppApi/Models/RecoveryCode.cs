using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class RecoveryCode : IUserOwnedEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string CodeHash { get; set; } = string.Empty;

    public bool Used { get; set; } = false;

    [Required]
    public DateTime CreatedAt { get; set; }
}
