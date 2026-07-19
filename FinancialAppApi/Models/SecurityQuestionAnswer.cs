using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class SecurityQuestionAnswer : IUserOwnedEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public int QuestionId { get; set; }

    [Required]
    public string AnswerHash { get; set; } = string.Empty;
}
