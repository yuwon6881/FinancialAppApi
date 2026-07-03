using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class BiometricCredential
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string CredentialId { get; set; } = string.Empty;

    public string PublicKey { get; set; } = string.Empty;

    public long Counter { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}
