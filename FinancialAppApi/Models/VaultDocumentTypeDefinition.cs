using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public sealed class VaultDocumentTypeDefinition : IUserOwnedEntity
{
    [Key]
    [StringLength(64)]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [StringLength(40)]
    public string Name { get; set; } = string.Empty;
}
