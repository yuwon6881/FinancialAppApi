using FinancialAppApi.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class AuthAccountService
{
    public static List<string> GetAvailableSecurityQuestions()
    {
        // Append-only: the position (index) of each entry is the persisted QuestionId,
        // so never reorder or remove existing questions — only add new ones at the end.
        return new List<string>
        {
            "What was the name of your first pet?",
            "What was the model of your first car?",
            "In what city were you born?",
            "What is your mother's maiden name?",
            "What was the name of your elementary school?",
            "What was your childhood nickname?",
            "What is the name of your favourite teacher?",
            "What was the name of the street you grew up on?",
            "What is your favourite book?",
            "What was the name of your first employer?",
            "In what city did your parents meet?",
            "What is the name of your favourite childhood friend?",
            "What was your dream job as a child?",
            "What is the make and model of your dream car?",
            "What is your favourite movie?",
            "What was the destination of your first flight?"
        };
    }

    public async Task<IActionResult> SetupSecurityQuestionsAsync(
        string? username,
        List<QuestionAnswerDto> answers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(username)) return new UnauthorizedResult();
        var user = await _context.AppUsers
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user == null) return new UnauthorizedResult();

        if (user.HasSetupSecurityQuestions)
        {
            return new BadRequestObjectResult(new { message = "Security questions are already set up." });
        }

        if (answers == null || answers.Count != 3)
        {
            return new BadRequestObjectResult(new { message = "You must provide answers to exactly 3 questions." });
        }

        var uniqueQuestionIds = answers.Select(a => a.QuestionId).Distinct().Count();
        if (uniqueQuestionIds != 3)
        {
            return new BadRequestObjectResult(new { message = "You must select 3 distinct questions." });
        }

        var availableQuestionsCount = GetAvailableSecurityQuestions().Count;
        if (answers.Any(a => a.QuestionId < 0 || a.QuestionId >= availableQuestionsCount))
        {
             return new BadRequestObjectResult(new { message = "Invalid question selected." });
        }

        foreach (var answer in answers)
        {
            if (string.IsNullOrWhiteSpace(answer.Answer))
            {
                return new BadRequestObjectResult(new { message = "Answers cannot be empty." });
            }

            var normalized = SecurityQuestionNormalization.NormalizeAnswer(answer.Answer);
            var hash = _passwordHasher.HashPassword(user.Username, normalized);

            _context.SecurityQuestionAnswers.Add(new SecurityQuestionAnswer
            {
                UserId = user.Id,
                QuestionId = answer.QuestionId,
                AnswerHash = hash
            });
        }

        user.HasSetupSecurityQuestions = true;
        await _context.SaveChangesAsync(cancellationToken);

        return new OkObjectResult(new { message = "Security questions configured successfully." });
    }

    public async Task<IActionResult> GetSecurityQuestionsForRecoveryAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return new BadRequestObjectResult(new { message = "Username is required." });

        var normalizedUsername = username.Trim().ToUpperInvariant();
        var user = await _context.AppUsers
            .FirstOrDefaultAsync(
                u => u.NormalizedUsername == normalizedUsername,
                cancellationToken);

        if (user == null)
        {
            // Do not reveal user existence; just return empty or generic response
            return new BadRequestObjectResult(new { message = "If the user exists and has security questions, they will be displayed." });
        }

        if (!user.HasSetupSecurityQuestions)
        {
             return new BadRequestObjectResult(new { message = "This user has not set up security questions." });
        }

        var userQuestions = await _context.SecurityQuestionAnswers
            .Where(sqa => sqa.UserId == user.Id)
            .Select(sqa => sqa.QuestionId)
            .ToListAsync(cancellationToken);

        var available = GetAvailableSecurityQuestions();
        var questionsToAsk = userQuestions.Select(id => new { QuestionId = id, Question = available[id] }).ToList();

        return new OkObjectResult(new { username = user.Username, questions = questionsToAsk });
    }

    public async Task<IActionResult> VerifySecurityQuestionsAndResetPasswordAsync(
        string username,
        List<QuestionAnswerDto> answers,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(newPassword))
        {
            return new BadRequestObjectResult(new { message = "Username and new password are required." });
        }

        var normalizedUsername = username.Trim().ToUpperInvariant();
        var user = await _context.AppUsers
            .FirstOrDefaultAsync(
                u => u.NormalizedUsername == normalizedUsername,
                cancellationToken);

        if (user == null || !user.HasSetupSecurityQuestions)
        {
            return new BadRequestObjectResult(new { message = "Invalid attempt." });
        }

        if (answers == null || answers.Count < 3)
        {
            return new BadRequestObjectResult(new { message = "You must answer all 3 questions." });
        }

        var storedAnswers = await _context.SecurityQuestionAnswers
            .Where(sqa => sqa.UserId == user.Id)
            .ToListAsync(cancellationToken);

        if (storedAnswers.Count != 3)
        {
            return new BadRequestObjectResult(new { message = "Internal error regarding stored questions." });
        }

        int correctAnswers = 0;

        foreach (var answer in answers)
        {
            var stored = storedAnswers.FirstOrDefault(s => s.QuestionId == answer.QuestionId);
            if (stored != null)
            {
                var normalized = SecurityQuestionNormalization.NormalizeAnswer(answer.Answer);
                var result = _passwordHasher.VerifyHashedPassword(user.Username, stored.AnswerHash, normalized);
                if (result != PasswordVerificationResult.Failed)
                {
                    correctAnswers++;
                }
            }
        }

        // Require at least 2 out of 3 correct answers
        if (correctAnswers >= 2)
        {
            user.PasswordHash = _passwordHasher.HashPassword(user.Username, newPassword);
            user.FailedLoginAttempts = 0;
            user.LockedUntil = null;
            user.PasswordVerificationFailedAttempts = 0;
            user.PasswordVerificationLockedUntil = null;
            user.TwoFactorFailedAttempts = 0;
            user.TwoFactorLockedUntil = null;

            await _context.SaveChangesAsync(cancellationToken);
            // This endpoint is unauthenticated (no current-user DB scope), so revoke by the
            // verified user's id rather than the ambient scope.
            await _authSessionService.RevokeAllSessionsForUserAsync(
                user.Id,
                cancellationToken);

            return new OkObjectResult(new { message = "Password reset successfully." });
        }

        return new BadRequestObjectResult(new { message = "Answers are incorrect." });
    }
}
