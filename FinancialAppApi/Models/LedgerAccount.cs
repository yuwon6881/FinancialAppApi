using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

/// <summary>
/// A named real-world container for one ledger bucket. The balance is derived entirely from
/// ledger transactions; the account itself stores no balance and no accrual state.
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

    public bool IsArchived { get; set; }

    // Only a credit card carries a limit. It is the lender's ceiling, not money the user holds, so
    // nothing adds it to a balance; it only bounds what the card can still pay out.
    public decimal? CreditLimit { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
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
    public const string CreditCard = "CreditCard";
    public const string Other = "Other";

    public static readonly string[] Values = [Bank, EWallet, Cash, Card, CreditCard, Other];

    // A credit card holds debt. Stability is a reserve and Growth is savings, so debt inside either
    // would distort the reload queue and contribution figures; cards sit only in spending buckets.
    public static readonly string[] CreditCardBuckets = ["Essentials", "Rewards"];

    public const decimal MaxCreditLimit = 9999999999.99m;

    public static bool IsCreditCard(string? value) =>
        string.Equals(value, CreditCard, StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedInBucket(string? kind, string? bucket) =>
        !IsCreditCard(kind) || CreditCardBuckets.Contains(bucket, StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string? value) =>
        value is not null && Values.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string value) =>
        Values.First(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
}
