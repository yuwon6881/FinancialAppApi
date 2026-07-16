using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class ReceiptScanJob
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Status { get; set; } = "queued";

    [Required]
    public string MimeType { get; set; } = "image/jpeg";

    // Stored as PostgreSQL bytea instead of base64 text, avoiding roughly 33% encoding
    // overhead. This payload is cleared immediately when processing reaches a terminal state.
    public byte[]? ImageData { get; set; }

    public string? ResultJson { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }
}
