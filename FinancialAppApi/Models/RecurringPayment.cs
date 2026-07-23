using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class RecurringPayment : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public decimal Amount { get; set; }

    [Required]
    public string Frequency { get; set; } = string.Empty; // "Monthly" | "Annually"

    [Required]
    [StringLength(100)]
    public string Category { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string LedgerCategory { get; set; } = string.Empty;

    [Required]
    public string NextDueDate { get; set; } = string.Empty;

    [Required]
    public int DueDate { get; set; } // Day of the month (e.g. 14)

    [Required]
    public string StartDate { get; set; } = string.Empty; // Start date (yyyy-MM-dd)

    [Required]
    public bool Active { get; set; }

    public string? EndDate { get; set; } // End date (yyyy-MM-dd, optional)

    // Per-payment opt-in for push reminders. Defaults to false: the account-level toggle
    // (FinancialSetting.PushRemindersEnabled) and this payment-level toggle must both be on
    // before any reminder is dispatched.
    [Required]
    public bool PushReminderEnabled { get; set; }

    // "Once" sends a single catch-up reminder anywhere inside the lead window; "Countdown"
    // sends one reminder per day counting down from PushReminderLeadDays to 0 with no backfill.
    [Required]
    [StringLength(20)]
    public string PushReminderMode { get; set; } = "Once";

    // How many days before the due date reminders may start. Restricted to 1/2/3/7 by both the
    // controller validation and a database check constraint.
    [Required]
    public int PushReminderLeadDays { get; set; } = 1;
}
