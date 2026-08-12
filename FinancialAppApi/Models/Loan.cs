using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

/// <summary>
/// User-supplied loan terms. The outstanding balance is deliberately not stored: it is derived by
/// replaying the linked recurring-payment settlement transactions.
/// </summary>
public class Loan : IUserOwnedEntity
{
    [Key]
    [StringLength(100)]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    // Deliberately not a foreign key: deleting the bill must leave the loan and its history intact.
    [Required]
    [StringLength(100)]
    public string RecurringPaymentId { get; set; } = string.Empty;

    [Required]
    public decimal OpeningPrincipal { get; set; }

    [Required]
    public DateOnly TrackingStartDate { get; set; }

    /// <summary>Annual percentage rate, entered as a percentage such as 5.5 rather than 0.055.</summary>
    [Required]
    public decimal AnnualRatePercent { get; set; }

    [Required]
    public int TermPeriods { get; set; }

    [Required]
    [StringLength(30)]
    public string InterestMethod { get; set; } = LoanInterestMethod.ReducingBalance;
}

public static class LoanInterestMethod
{
    public const string ReducingBalance = "ReducingBalance";
    public const string Flat = "Flat";
}
