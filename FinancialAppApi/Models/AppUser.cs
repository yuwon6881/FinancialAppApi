using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class AppUser
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string PasswordHash { get; set; } = string.Empty;
}
