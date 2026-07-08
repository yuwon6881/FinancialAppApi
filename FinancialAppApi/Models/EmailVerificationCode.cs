using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class EmailVerificationCode
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string CodeHash { get; set; } = string.Empty;

    [Required]
    public DateTime ExpiresAt { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; }

    public int Attempts { get; set; } = 0;
}
