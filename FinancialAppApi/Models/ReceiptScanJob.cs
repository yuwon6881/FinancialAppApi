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

    public string? ImageBase64 { get; set; }

    public string? ResultJson { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }
}
