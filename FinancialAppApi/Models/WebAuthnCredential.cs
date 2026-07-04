using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class WebAuthnCredential
{
    [Key]
    public byte[] CredentialId { get; set; } = Array.Empty<byte>();

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();

    [Required]
    public long SignCount { get; set; }

    public string? DeviceLabel { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; }
}
