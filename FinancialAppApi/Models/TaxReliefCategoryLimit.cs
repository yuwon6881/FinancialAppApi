using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Models;

/// <summary>
/// A user-maintained tax-relief category limit for one assessment year.
/// Rows are copied when a year first diverges from an inherited prior-year configuration.
/// </summary>
public sealed class TaxReliefCategoryLimit : IUserOwnedEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [StringLength(80)]
    public string CategoryId { get; set; } = string.Empty;

    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [Precision(18, 2)]
    public decimal Limit { get; set; }

    [Required]
    [StringLength(300)]
    public string Detail { get; set; } = string.Empty;

    [Required]
    public int TaxYear { get; set; }
}
