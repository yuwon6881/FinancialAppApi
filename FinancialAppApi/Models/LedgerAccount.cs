using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

/// <summary>
/// A named real-world container for one ledger bucket. The balance is derived from ledger
/// transactions; interest settings describe the automatic credits written back to that ledger.
/// </summary>
public sealed class LedgerAccount : IUserOwnedEntity
{
    [Key]
    [StringLength(100)]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(20)]
    public string Bucket { get; set; } = string.Empty;

    [Required]
    [StringLength(20)]
    public string Kind { get; set; } = LedgerAccountKind.Bank;

    public bool InterestEnabled { get; set; }

    public decimal InterestRatePercent { get; set; }

    [Required]
    [StringLength(10)]
    public string InterestFrequency { get; set; } = LedgerAccountInterestFrequency.Monthly;

    // The next posting date is server-owned. Keeping it on the account makes catch-up posting
    // idempotent even when nobody opens the app for several periods or a period earns less than
    // one cent. The remainder preserves sub-cent daily accrual until it can be posted.
    public DateOnly? InterestNextAccrualDate { get; set; }

    public decimal InterestRemainder { get; set; }

    public bool IsDefault { get; set; }

    public bool IsArchived { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Interest posting frequencies are stored as strings deliberately, matching the rest of the
/// account vocabulary and avoiding an EF enum conversion for one field.
/// </summary>
public static class LedgerAccountInterestFrequency
{
    public const string Daily = "Daily";
    public const string Monthly = "Monthly";
    public const string Yearly = "Yearly";

    public static readonly string[] Values = [Daily, Monthly, Yearly];

    public static bool IsValid(string? value) =>
        value is not null && Values.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string value) =>
        Values.First(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));

    public static DateOnly NextDate(DateOnly date, string frequency) =>
        Normalize(frequency) switch
        {
            Daily => date.AddDays(1),
            Monthly => date.AddMonths(1),
            Yearly => date.AddYears(1),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unsupported interest frequency."),
        };
}

/// <summary>
/// Stored as strings deliberately: account kinds are a wire/database vocabulary, not a CLR
/// enum that needs a conversion and can drift from the client.
/// </summary>
public static class LedgerAccountKind
{
    public const string Bank = "Bank";
    public const string EWallet = "EWallet";
    public const string Cash = "Cash";
    public const string Card = "Card";
    public const string Other = "Other";

    public static readonly string[] Values = [Bank, EWallet, Cash, Card, Other];

    public static bool IsValid(string? value) =>
        value is not null && Values.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string value) =>
        Values.First(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
}
