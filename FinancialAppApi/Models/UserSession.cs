using System;
using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class UserSession
{
    [Key]
    public string Token { get; set; } = string.Empty;

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public DateTime CreatedAt { get; set; }

    [Required]
    public DateTime ExpiresAt { get; set; }

    [Required]
    public bool IsLocked { get; set; } = false;
}
