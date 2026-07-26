using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class FinancialSetting : IUserOwnedEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public decimal TargetStabilityFund { get; set; }

    [Required]
    public string SelectedMonth { get; set; } = "Jun";

    [Required]
    public int SelectedYear { get; set; } = 2026;

    [Required]
    public decimal EssentialsAlloc { get; set; } = 0.50m;

    [Required]
    public decimal GrowthAlloc { get; set; } = 0.25m;

    [Required]
    public decimal StabilityAlloc { get; set; } = 0.15m;

    [Required]
    public decimal RewardsAlloc { get; set; } = 0.10m;

    public string StabilityOverflowRedirect { get; set; } = "Split: Growth 50%, Rewards 50%";

    [Required]
    public int CycleDay { get; set; } = 28; // Day of the month representing the cycle start (e.g. 28th)

    // Dark theme preference. Null means "never chosen" so the client can fall back to
    // the OS/browser color scheme (matching the login screen) instead of forcing light.
    public bool? DarkMode { get; set; }

    [Required]
    public bool HideSensitive { get; set; } = true;

    [Required]
    public string Currency { get; set; } = "USD";

    [Required]
    public bool VibrationEnabled { get; set; } = true;

    // The current-cycle key ("yyyy-MM", e.g. "2026-08") the user last acknowledged an
    // end-of-cycle summary for. Null means the user has never seen one, so the client
    // silently adopts the current cycle instead of surfacing a summary for a cycle the
    // user never actually used. Persisting this server-side (rather than per-device)
    // guarantees the summary fires exactly once per cycle across all of a user's devices.
    public string? LastSummaryCycleSeen { get; set; }

    // Legacy/denormalized account status retained for compatibility. Enabled device
    // subscriptions are authoritative; subscribe/unsubscribe keeps this value in sync.
    [Required]
    public bool PushRemindersEnabled { get; set; }
}
