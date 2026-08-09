using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

// Durable transaction-change facts captured in the same SaveChanges call as the ledger mutation.
// The after-response processor removes them only after it has evaluated the resulting cycle total,
// so a stopped request can be recovered by the existing scheduled push dispatch.
public sealed class CategoryLimitAlertEvaluation : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [StringLength(100)]
    public string? PreviousCategory { get; set; }

    [StringLength(200)]
    public string? PreviousLedgerCategory { get; set; }

    public DateTime? PreviousDate { get; set; }

    public decimal? PreviousAmount { get; set; }

    [StringLength(100)]
    public string? CurrentCategory { get; set; }

    [StringLength(200)]
    public string? CurrentLedgerCategory { get; set; }

    public DateTime? CurrentDate { get; set; }

    public decimal? CurrentAmount { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; }
}

// One user-visible push event. Several category milestones created by one bulk mutation share one
// event, preventing a restore/import from producing a burst of notifications.
public sealed class CategoryLimitAlertEvent : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [StringLength(7)]
    public string CycleKey { get; set; } = string.Empty;

    [Required]
    [StringLength(160)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(240)]
    public string Body { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string Tag { get; set; } = string.Empty;

    [StringLength(100)]
    public string? CategoryName { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; }

    [Required]
    public DateTime ExpiresAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}

// The cycle latch. A unique category+cycle+milestone row means a category can alert at most once
// when it approaches its guide and once when it reaches or crosses it.
public sealed class CategoryLimitAlertMilestone : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string EventId { get; set; } = string.Empty;

    [Required]
    [StringLength(7)]
    public string CycleKey { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string CategoryName { get; set; } = string.Empty;

    [Required]
    [StringLength(16)]
    public string Milestone { get; set; } = string.Empty;
}

// Insert-before-send per-device claim, matching the recurring reminder concurrency strategy.
public sealed class CategoryLimitAlertDelivery : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string EventId { get; set; } = string.Empty;

    [Required]
    public string SubscriptionId { get; set; } = string.Empty;

    [Required]
    public DateTime SentAt { get; set; }
}
