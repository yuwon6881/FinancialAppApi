using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class AuthAccountService
{
    public async Task<IActionResult> GetTwoFactorStatusAsync(
        string? username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedResult();
        }

        var user = await _context.AppUsers
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user == null)
        {
            return new UnauthorizedResult();
        }

        return new OkObjectResult(new { enabled = user.TotpEnabled });
    }

    public async Task<IActionResult> SetupTotpAsync(
        string? username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedResult();
        }

        var user = await _context.AppUsers
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user == null)
        {
            return new UnauthorizedResult();
        }
        if (user.TotpEnabled)
        {
            return new BadRequestObjectResult(new { message = "Two-factor authentication is already enabled." });
        }

        var secret = _totpService.GenerateSecret();
        user.PendingTotpSecret = _secretProtector.Protect(secret);
        await _context.SaveChangesAsync(cancellationToken);

        return new OkObjectResult(new { secret, otpauthUri = _totpService.BuildOtpAuthUri(secret, username) });
    }

    public async Task<IActionResult> EnableTotpAsync(
        string? username,
        string? code,
        string? currentToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedResult();
        }

        var user = await _context.AppUsers
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user == null || string.IsNullOrEmpty(user.PendingTotpSecret))
        {
            return new BadRequestObjectResult(new { message = "Start two-factor setup first." });
        }

        var secret = _secretProtector.Unprotect(user.PendingTotpSecret);
        if (!_totpService.ValidateCode(secret, code ?? string.Empty, out var timeStepMatched))
        {
            return new BadRequestObjectResult(new { message = "Invalid code." });
        }

        user.TotpSecret = user.PendingTotpSecret;
        user.PendingTotpSecret = null;
        user.TotpEnabled = true;
        user.LastTotpTimeStep = timeStepMatched;
        user.TwoFactorFailedAttempts = 0;
        user.TwoFactorLockedUntil = null;

        var recoveryCodes = await _recoveryCodeService.RegenerateAsync(
            username,
            user.Id,
            cancellationToken);
        await _authSessionService.RevokeOtherSessionsAsync(
            username,
            currentToken,
            cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new OkObjectResult(new { enabled = true, recoveryCodes });
    }

    public async Task<IActionResult> DisableTotpAsync(
        string? username,
        string? password,
        string? code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedResult();
        }

        var user = await _context.AppUsers
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user == null || !user.TotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
        {
            return new BadRequestObjectResult(new { message = "Two-factor authentication is not enabled." });
        }

        var passwordFailure = await VerifyPasswordForSensitiveActionAsync(user, password, cancellationToken);
        if (passwordFailure != null) return passwordFailure;

        var secret = _secretProtector.Unprotect(user.TotpSecret);
        var validTotp = _totpService.ValidateCode(secret, code ?? string.Empty);
        var validRecovery = !validTotp && await _recoveryCodeService.TryConsumeAsync(
            username,
            code ?? string.Empty,
            user.Id,
            cancellationToken);
        if (!validTotp && !validRecovery)
        {
            return new BadRequestObjectResult(new { message = "Invalid code." });
        }

        user.TotpEnabled = false;
        user.TotpSecret = null;
        user.PendingTotpSecret = null;
        user.LastTotpTimeStep = null;
        user.TwoFactorFailedAttempts = 0;
        user.TwoFactorLockedUntil = null;
        await _context.SaveChangesAsync(cancellationToken);
        await _recoveryCodeService.DeleteAllAsync(username, user.Id, cancellationToken);

        return new OkObjectResult(new { message = "Two-factor authentication disabled." });
    }

    public async Task<IActionResult> RegenerateRecoveryCodesAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedResult();
        }

        var user = await _context.AppUsers
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user == null || !user.TotpEnabled)
        {
            return new BadRequestObjectResult(new { message = "Two-factor authentication is not enabled." });
        }

        var passwordFailure = await VerifyPasswordForSensitiveActionAsync(user, password, cancellationToken);
        if (passwordFailure != null) return passwordFailure;

        var codes = await _recoveryCodeService.RegenerateAsync(
            username,
            user.Id,
            cancellationToken);
        return new OkObjectResult(new { recoveryCodes = codes });
    }
}
