using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public sealed class AiConversation : IUserOwnedEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserId { get; set; } = string.Empty;

    public int Version { get; set; }

    public string? StateJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AiConversationTurn> Turns { get; set; } = [];
}

public sealed class AiConversationTurn : IUserOwnedEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserId { get; set; } = string.Empty;

    public Guid ConversationId { get; set; }

    public AiConversation Conversation { get; set; } = null!;

    [Required]
    [StringLength(64)]
    public string ClientTurnId { get; set; } = string.Empty;

    [Required]
    [StringLength(2000)]
    public string UserMessage { get; set; } = string.Empty;

    [Required]
    public string AssistantReply { get; set; } = string.Empty;

    [Required]
    public string ActionsJson { get; set; } = "[]";

    [Required]
    [StringLength(16)]
    public string Status { get; set; } = "Completed";

    public DateTime? ActionsResolvedAt { get; set; }

    public DateTime? ActionsDismissedAt { get; set; }

    public bool CloseChat { get; set; }

    [StringLength(80)]
    public string? Intent { get; set; }

    [StringLength(32)]
    public string? Topic { get; set; }

    public string? FacetsJson { get; set; }

    public string? KeywordsJson { get; set; }

    public bool SensitiveMode { get; set; }

    public int ConversationVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }
}
