using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

// Bridges a password-verified login and the eventual session it will produce once a TOTP/recovery
// code is confirmed. The Id doubles as the opaque "pendingToken" handed to the client -- it grants
// no access on its own, only the ability to attempt the second factor for this specific login.
public class PendingTwoFactor
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Username { get; set; } = string.Empty;

    public string? DeviceId { get; set; }

    public string? DeviceName { get; set; }

    [Required]
    public DateTime ExpiresAt { get; set; }

    public int Attempts { get; set; } = 0;
}
