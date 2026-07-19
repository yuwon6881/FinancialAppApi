using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class ReceiptScanJob : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Status { get; set; } = "queued";

    [Required]
    public string MimeType { get; set; } = "image/jpeg";

    // Jobs keep only this private Supabase Storage object path in Postgres. The
    // object itself is deleted as soon as OCR reaches a terminal state.
    [StringLength(512)]
    public string? StorageObjectPath { get; set; }

    public string? ResultJson { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }
}
