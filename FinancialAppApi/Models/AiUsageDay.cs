using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

// One user's Ask AI token use for one UTC day: the basis of the daily budget and of cost
// visibility. Aged out by LedgerRetentionService on Date.
public sealed class AiUsageDay : IUserOwnedEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserId { get; set; } = string.Empty;

    public DateOnly Date { get; set; }

    public long InputTokens { get; set; }

    // Part of InputTokens served from the provider's prompt cache (billed at a discount).
    public long CachedTokens { get; set; }

    // Includes reasoning tokens, which the provider bills as output.
    public long OutputTokens { get; set; }

    public long ReasoningTokens { get; set; }

    public int Calls { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
