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

    // The sender selects the provider payload from the registered client transport. Historical
    // rows and older clients remain web subscriptions unless a native client explicitly updates
    // the row.
    [Required]
    [StringLength(16)]
    public string Platform { get; set; } = PushPlatform.Web;

    // "This device holds a live send credential and wants at least one kind of notification."
    // Invariant, maintained by PushSubscriptionService: Enabled == (BillRemindersEnabled ||
    // CategoryAlertsEnabled). Kept as its own column rather than derived in every query because
    // the dispatcher, both delivery ledgers, and the FCM "unregistered token" path all key off
    // one cheap boolean, and a device whose token FCM has rejected is off for every channel at
    // once regardless of what the user asked for.
    [Required]
    public bool Enabled { get; set; } = true;

    // The two notification kinds are opted into separately, and per device. A phone that asked
    // for spending alerts must not start receiving bill reminders, and a desktop that asked for
    // neither must not be told it is receiving either -- which is what a single account-wide
    // consent flag could not express.
    [Required]
    public bool BillRemindersEnabled { get; set; } = true;

    [Required]
    public bool CategoryAlertsEnabled { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; }
}

public static class PushPlatform
{
    public const string Web = "web";
    public const string Android = "android";
    public const string Ios = "ios";

    public static bool IsKnown(string? platform) =>
        string.Equals(platform, Web, StringComparison.OrdinalIgnoreCase)
        || string.Equals(platform, Android, StringComparison.OrdinalIgnoreCase)
        || string.Equals(platform, Ios, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? platform) =>
        platform?.Trim().ToLowerInvariant() switch
        {
            Android => Android,
            Ios => Ios,
            _ => Web
        };
}
