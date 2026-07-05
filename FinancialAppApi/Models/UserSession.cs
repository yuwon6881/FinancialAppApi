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

    // Set only for sessions issued via WebAuthn login (null for password
    // logins). Lets a fresh fingerprint login on the same enrolled device
    // replace whatever session it last issued instead of piling up a new
    // row every time local storage is cleared/reinstalled.
    public byte[]? CredentialId { get; set; }
}
