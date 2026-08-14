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

    /// <summary>
    /// The live ledger account that pays this recurring item. The bucket and account are both
    /// stored so a settlement never has to infer placement from a bucket default.
    /// </summary>
    [Required]
    [StringLength(100)]
    public string AccountId { get; set; } = string.Empty;

    public string? NextDueDate { get; set; }

    // Existing schedules start authoritative pending-occurrence tracking at the ledger migration;
    // new/edited/resumed schedules move this boundary forward instead of inventing missed bills.
    public DateOnly OccurrenceTrackingStartDate { get; set; }

    [Required]
    public int DueDate { get; set; } // Day of the month (e.g. 14)

    [Required]
    public string StartDate { get; set; } = string.Empty; // Start date (yyyy-MM-dd)

    [Required]
    public bool Active { get; set; }

    public string? EndDate { get; set; } // End date (yyyy-MM-dd, optional)

    // "AutoDeduct" means the money leaves the account on its own; "Manual" means the user sends it
    // each cycle. Only Manual payments can be settled ahead of their due date -- a direct debit
    // cannot be brought forward, so paying one early would post a ledger row for money the bank is
    // still going to take on the real due date. Enforced in RecurringPaymentPayEarlyService.
    [Required]
    [StringLength(20)]
    public string PaymentMode { get; set; } = RecurringPaymentMode.Manual;

    // Per-payment opt-in for push reminders. Defaults to false: this toggle and at least one
    // enabled PushSubscription (the device opt-in) must both be present before any reminder is
    // dispatched. There is no account-level push flag -- enabled devices are authoritative.
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

// Stored as a string with a database check constraint rather than a CLR enum, matching the
// SavingsGoalStatus pattern -- this project has no enum-to-string conversion infrastructure.
public static class RecurringPaymentMode
{
    public const string AutoDeduct = "AutoDeduct";
    public const string Manual = "Manual";
}
