using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

/// <summary>
/// A long-lived tax document stored in the Document Vault.
/// </summary>
public class VaultDocument : IUserOwnedEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [StringLength(512)]
    public string StorageObjectPath { get; set; } = string.Empty;

    [Required]
    [StringLength(255)]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string ContentType { get; set; } = string.Empty;

    [Required]
    public long SizeBytes { get; set; }

    [Required]
    [StringLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    [Required]
    public int TaxYear { get; set; }

    [Required]
    [StringLength(40)]
    public string DocumentType { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Notes { get; set; }

    // Not a foreign key. Deleting a transaction should leave its documents intact.
    public string? TransactionId { get; set; }

    [Required]
    public DateTime UploadedAt { get; set; }

    [Required]
    public DateOnly RetentionUntil { get; set; }

    [StringLength(64)]
    public string? ClientKey { get; set; }
}
