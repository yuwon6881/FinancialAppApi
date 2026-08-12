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

    // The deployed schema adds a restrictive NOT VALID foreign key: legacy orphaned loans remain
    // recoverable, while linked bills cannot be deleted and new links must reference a real bill.
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

    /// <summary>
    /// How the rate was shown when the terms were entered. AnnualRatePercent remains the
    /// authoritative stored value; no amortization code reads this display metadata.
    /// </summary>
    [Required]
    [StringLength(10)]
    public string RateBasis { get; set; } = LoanRateBasis.Yearly;

    [Required]
    public int TermPeriods { get; set; }

    [Required]
    [StringLength(30)]
    public string InterestMethod { get; set; } = LoanInterestMethod.ReducingBalance;

    // These fields are captured from the linked bill when the loan is created. They deliberately
    // do not follow later bill edits: loan interest and occurrence order must remain reproducible
    // even when the reminder schedule is renamed, paused, or deleted.
    [StringLength(20)]
    public string? ScheduleFrequency { get; set; }

    public int? ScheduleDueDay { get; set; }

    public DateOnly? ScheduleStartDate { get; set; }

    [Required]
    [StringLength(20)]
    public string ScheduleStatus { get; set; } = LoanScheduleStatus.Incomplete;
}

public static class LoanInterestMethod
{
    public const string ReducingBalance = "ReducingBalance";
    public const string Flat = "Flat";
    public const string ReducingBalanceDaily = "ReducingBalanceDaily";
    public const string InterestOnly = "InterestOnly";

    public static bool IsKnown(string? value) =>
        value is ReducingBalance or Flat or ReducingBalanceDaily or InterestOnly;
}

public static class LoanRateBasis
{
    public const string Yearly = "Yearly";
    public const string Monthly = "Monthly";

    public static bool IsKnown(string? value) =>
        value is Yearly or Monthly;
}

public static class LoanScheduleStatus
{
    public const string Complete = "Complete";
    public const string Incomplete = "Incomplete";

    public static bool IsKnown(string? value) =>
        value is Complete or Incomplete;
}
