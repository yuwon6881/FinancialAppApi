using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models
{
    // A dated savings commitment funded out of the SAME Rewards pool the wishlist draws from
    // (car maintenance in 3 months, a house deposit in 6 years). It is deliberately NOT a fifth
    // budget bucket: the four ledger allocations (Essentials/Growth/Stability/Rewards) are
    // untouched, and a goal is purely an *earmark* -- a claim on Rewards money the user already
    // has. The governing invariant is enforced in SavingsGoalService:
    //
    //     SUM(EarmarkedAmount of active goals) <= current Rewards balance
    //
    // so a goal can never lay claim to money that is not there, and
    // (rewardsBalance - SUM(earmarked)) is the genuinely spontaneous "free to spend" remainder
    // that wishlist rewards are measured against.
    public class SavingsGoal : IUserOwnedEntity
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string UserId { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        /// <summary>What the goal needs in total, e.g. 1200 for a car service.</summary>
        [Required]
        public decimal TargetAmount { get; set; }

        /// <summary>
        /// How much of the Rewards pool is currently claimed by this goal. Only ever changed by an
        /// explicit operation (create, contribute, per-cycle funding, complete) -- never derived on
        /// read, so the number the user saw last is the number that is still there.
        /// </summary>
        [Required]
        public decimal EarmarkedAmount { get; set; }

        /// <summary>The date the money needs to be ready. Drives the required-per-cycle pace.</summary>
        [Required]
        public DateTime TargetDate { get; set; }

        /// <summary>High/Medium/Low. Decides who gets funded first when a cycle cannot cover every goal.</summary>
        [Required]
        [StringLength(10)]
        public string Priority { get; set; } = "Medium";

        /// <summary>active | completed. Completed goals release their earmark and stop being paced.</summary>
        [Required]
        [StringLength(16)]
        public string Status { get; set; } = SavingsGoalStatus.Active;

        /// <summary>
        /// A repeating commitment (quarterly car service, annual insurance). On completion the
        /// target date rolls forward by <see cref="RecurrenceMonths"/> and the earmark resets to
        /// zero instead of the goal closing, so the user does not re-create it four times a year.
        /// </summary>
        [Required]
        public bool IsRecurring { get; set; }

        [Required]
        public int RecurrenceMonths { get; set; } = 12;

        /// <summary>
        /// The original calendar day for a recurring goal. Month ends are clamped to the last
        /// valid day for that month, but this anchor is retained so a goal created on the 31st can
        /// return to the 31st when a later month has one.
        /// </summary>
        public int? RecurrenceDayOfMonth { get; set; }

        /// <summary>
        /// Cycle key ("yyyy-MM") that <see cref="CycleFundedAmount"/> is measured against. A key
        /// that is not the current cycle means the tally has rolled over and counts as zero.
        /// </summary>
        [StringLength(7)]
        public string? CycleFundedKey { get; set; }

        /// <summary>
        /// Net amount added to this goal's earmark during <see cref="CycleFundedKey"/>, from every
        /// source: automatic per-cycle funding *and* manual top-ups, less any releases.
        ///
        /// This is what makes "Fund this cycle" precise rather than a once-per-cycle latch. The
        /// outstanding amount for a goal is `requiredPerCycle - CycleFundedAmount`, so:
        /// a goal already topped up by hand is skipped, releasing money makes it fundable again,
        /// and a second tap after a full funding round contributes nothing. Floored at zero so
        /// releasing money that was set aside in an *earlier* cycle cannot inflate this cycle's
        /// entitlement.
        /// </summary>
        [Required]
        public decimal CycleFundedAmount { get; set; }

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? CompletedAt { get; set; }

        // Client-supplied idempotency key for offline creates, same rationale as WishlistItem:
        // the int PK is server-generated, so a lost-response retry needs a stable client key to
        // dedupe against instead of inserting a second goal.
        [StringLength(64)]
        public string? ClientKey { get; set; }
    }

    public static class SavingsGoalStatus
    {
        public const string Active = "active";
        public const string Completed = "completed";
    }
}
