using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

// A claim row written BEFORE the FCM call is attempted (never after), so a retried or
// concurrent dispatch run can never double-send the same reminder. The unique index on
// (UserId, RecurringPaymentId, OccurrenceDate, ActualOffsetDays, SubscriptionId) is what makes
// the claim atomic: the insert itself is the concurrency guard, not a separate lock.
public class PushReminderDelivery : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string RecurringPaymentId { get; set; } = string.Empty;

    [Required]
    public DateOnly OccurrenceDate { get; set; }

    // Days remaining until the occurrence is due, as observed at claim time (0 = due today).
    [Required]
    public int ActualOffsetDays { get; set; }

    [Required]
    public string SubscriptionId { get; set; } = string.Empty;

    [Required]
    public DateTime SentAt { get; set; }
}
