using System;
using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models
{
    public class WishlistItem
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string Name { get; set; } = string.Empty;

        [Required]
        public decimal Price { get; set; }


        [Required]
        public string Priority { get; set; } = "Medium"; // High, Medium, Low

        [Required]
        public bool IsPurchased { get; set; } = false;

        public DateTime? PurchasedAt { get; set; }

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public bool IsActive { get; set; } = false; // To track the single main active wishlist item
    }
}
