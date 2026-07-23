using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

// One row per (user, device). Re-subscribing the same device replaces the stored FCM token
// in place rather than accumulating duplicates, so a device's push target is always current.
public class PushSubscription : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string DeviceId { get; set; } = string.Empty;

    [Required]
    [StringLength(4096)]
    public string FcmToken { get; set; } = string.Empty;

    [Required]
    public bool Enabled { get; set; } = true;

    [Required]
    public DateTime CreatedAt { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; }
}
