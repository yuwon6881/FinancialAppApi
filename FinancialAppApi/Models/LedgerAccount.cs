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
    public const string Other = "Other";

    public static readonly string[] Values = [Bank, EWallet, Cash, Card, Other];

    public static bool IsValid(string? value) =>
        value is not null && Values.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string value) =>
        Values.First(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
}
