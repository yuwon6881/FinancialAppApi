using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class TransactionCategory : IUserOwnedEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    // Optional default spending guide for the user's current and future salary cycles.
    // Historical values are versioned in CategorySpendingGuide so changing this default
    // never rewrites an already-finished cycle's report.
    public decimal? CycleLimit { get; set; }

    // "both" | "inflow" | "outflow"
    [Required]
    [StringLength(20)]
    public string Type { get; set; } = CategoryFlowType.Both;
}

public static class CategoryFlowType
{
    public const string Both = "both";
    public const string Inflow = "inflow";
    public const string Outflow = "outflow";

    public static bool IsValid(string? type) => type is Both or Inflow or Outflow;

    public static string Normalize(string? type) => IsValid(type) ? type! : Both;
}

