using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class UserSession : IUserOwnedEntity
{
    [Key]
    public string Token { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    // Public identifier used by the sessions list/revoke API so the raw bearer token
    // (the live credential) never has to leave the server in a response body.
    public Guid Id { get; set; } = Guid.NewGuid();

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

    public string? DeviceId { get; set; }

    public string? DeviceName { get; set; }

    public DateTime? LastActiveAt { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }
}
