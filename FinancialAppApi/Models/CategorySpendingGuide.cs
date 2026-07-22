using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class CategorySpendingGuide : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string CategoryName { get; set; } = string.Empty;

    [Required]
    [StringLength(7)]
    public string EffectiveFromCycleKey { get; set; } = string.Empty;

    // Null is an effective-dated removal of a previously configured guide.
    public decimal? LimitAmount { get; set; }
}
