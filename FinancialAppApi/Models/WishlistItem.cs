using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models
{
    public class WishlistItem : IUserOwnedEntity
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string UserId { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public decimal Price { get; set; }


        [Required]
        public string Priority { get; set; } = "Medium"; // High, Medium, Low

        [Required]
        public bool IsPurchased { get; set; } = false;

        public DateTime? PurchasedAt { get; set; }

        public string? PurchaseTransactionId { get; set; }

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public bool IsActive { get; set; } = false; // To track the single main active wishlist item

        // Client-supplied idempotency key for offline creates. The int PK is server-generated,
        // so (unlike transactions) there is no client id to dedupe on; this stable key lets a
        // lost-response retry resolve to the already-created row instead of inserting a duplicate.
        [StringLength(64)]
        public string? ClientKey { get; set; }
    }
}
