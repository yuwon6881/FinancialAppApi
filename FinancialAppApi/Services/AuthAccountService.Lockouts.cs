using FinancialAppApi.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class AuthAccountService
{
    /// <summary>
    /// Shared attempt-limiter bookkeeping for the non-relational (in-memory provider) paths of
    /// the three failure recorders: increment the failure counter and, once it reaches
    /// <paramref name="maxAttempts"/>, engage the lockout and reset the counter. Returns true
    /// when the lockout engaged on this failure. The relational paths keep their own atomic
    /// ExecuteUpdate translations of this same shape, which differ per limiter (guarded WHERE
    /// clauses, preserve-vs-clear lockout semantics) and so are not unified here.
    /// </summary>
    private static bool RegisterFailureInMemory(
        Func<int> getFailedAttempts,
        Action<int> setFailedAttempts,
        Action<DateTime> engageLockout,
        int maxAttempts,
        DateTime lockedUntil)
    {
        var attempts = getFailedAttempts() + 1;
        if (attempts >= maxAttempts)
        {
            engageLockout(lockedUntil);
            setFailedAttempts(0);
            return true;
        }

        setFailedAttempts(attempts);
        return false;
    }

    private async Task<IActionResult> RecordTwoFactorFailureAsync(
        AppUser user,
        PendingTwoFactor pending,
        string message = "Invalid code",
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsRelational())
        {
            var maxAttempts = Math.Max(1, MaxTwoFactorAttempts);
            var now = DateTime.UtcNow;
            var lockedUntil = now.AddMinutes(TwoFactorLockoutMinutes);

            await _context.PendingTwoFactors
                .Where(candidate => candidate.Id == pending.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.Attempts, candidate => candidate.Attempts + 1),
                    cancellationToken);

            await _context.AppUsers
                .Where(candidate => candidate.Id == user.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        candidate => candidate.TwoFactorLockedUntil,
                        candidate => candidate.TwoFactorFailedAttempts + 1 >= maxAttempts
                            ? lockedUntil
                            : candidate.TwoFactorLockedUntil)
                    .SetProperty(
                        candidate => candidate.TwoFactorFailedAttempts,
                        candidate => candidate.TwoFactorFailedAttempts + 1 >= maxAttempts
                            ? 0
                            : candidate.TwoFactorFailedAttempts + 1),
                    cancellationToken);

            var lockState = await _context.AppUsers
                .AsNoTracking()
                .Where(candidate => candidate.Id == user.Id)
                .Select(candidate => candidate.TwoFactorLockedUntil)
                .SingleAsync(cancellationToken);
            if (lockState.HasValue && lockState.Value > now)
            {
                await _context.PendingTwoFactors
                    .Where(candidate => candidate.UserId == user.Id)
                    .ExecuteDeleteAsync(cancellationToken);
                return new ObjectResult(new { message = "Too many two-factor attempts. Please try again later." }) { StatusCode = 429 };
            }

            return new UnauthorizedObjectResult(new { message });
        }

        pending.Attempts += 1;
        var lockedOut = RegisterFailureInMemory(
            () => user.TwoFactorFailedAttempts,
            attempts => user.TwoFactorFailedAttempts = attempts,
            lockedUntil => user.TwoFactorLockedUntil = lockedUntil,
            MaxTwoFactorAttempts,
            DateTime.UtcNow.AddMinutes(TwoFactorLockoutMinutes));
        if (lockedOut)
        {
            await RemovePendingTwoFactorsAsync(user.Id, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            return new ObjectResult(new { message = "Too many two-factor attempts. Please try again later." }) { StatusCode = 429 };
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new UnauthorizedObjectResult(new { message });
    }

    private async Task RecordPasswordFailureAsync(
        AppUser user,
        CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational())
        {
            RegisterFailureInMemory(
                () => user.FailedLoginAttempts,
                attempts => user.FailedLoginAttempts = attempts,
                lockedUntil => user.LockedUntil = lockedUntil,
                Math.Max(1, MaxFailedLoginAttempts),
                DateTime.UtcNow.AddMinutes(LockoutMinutes));
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        var maxAttempts = Math.Max(1, MaxFailedLoginAttempts);
        var lockedUntil = DateTime.UtcNow.AddMinutes(LockoutMinutes);
        await _context.AppUsers
            .Where(candidate => candidate.Id == user.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    candidate => candidate.LockedUntil,
                    candidate => candidate.FailedLoginAttempts + 1 >= maxAttempts
                        ? lockedUntil
                        : candidate.LockedUntil)
                .SetProperty(
                    candidate => candidate.FailedLoginAttempts,
                    candidate => candidate.FailedLoginAttempts + 1 >= maxAttempts
                        ? 0
                        : candidate.FailedLoginAttempts + 1),
                cancellationToken);
    }

    private async Task<DateTime?> RecordPasswordVerificationFailureAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, MaxPasswordVerificationAttempts);
        var now = DateTime.UtcNow;
        var lockedUntil = now.AddMinutes(Math.Max(1, PasswordVerificationLockoutMinutes));

        if (_context.Database.IsRelational())
        {
            // ExecuteUpdate translates to one atomic UPDATE in PostgreSQL. Concurrent wrong
            // passwords therefore cannot overwrite each other's increments or bypass the
            // threshold through a read-modify-write race.
            await _context.AppUsers
                .Where(candidate =>
                    candidate.Id == userId &&
                    (!candidate.PasswordVerificationLockedUntil.HasValue ||
                     candidate.PasswordVerificationLockedUntil.Value <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        candidate => candidate.PasswordVerificationLockedUntil,
                        candidate => candidate.PasswordVerificationFailedAttempts + 1 >= maxAttempts
                            ? lockedUntil
                            : null)
                    .SetProperty(
                        candidate => candidate.PasswordVerificationFailedAttempts,
                        candidate => candidate.PasswordVerificationFailedAttempts + 1 >= maxAttempts
                            ? 0
                            : candidate.PasswordVerificationFailedAttempts + 1),
                    cancellationToken);

            return await _context.AppUsers
                .AsNoTracking()
                .Where(candidate => candidate.Id == userId)
                .Select(candidate => candidate.PasswordVerificationLockedUntil)
                .SingleAsync(cancellationToken);
        }

        var trackedUser = await _context.AppUsers
            .SingleAsync(candidate => candidate.Id == userId, cancellationToken);
        if (trackedUser.PasswordVerificationLockedUntil.HasValue)
        {
            if (trackedUser.PasswordVerificationLockedUntil.Value > now)
            {
                return trackedUser.PasswordVerificationLockedUntil;
            }

            trackedUser.PasswordVerificationLockedUntil = null;
            trackedUser.PasswordVerificationFailedAttempts = 0;
        }

        RegisterFailureInMemory(
            () => trackedUser.PasswordVerificationFailedAttempts,
            attempts => trackedUser.PasswordVerificationFailedAttempts = attempts,
            until => trackedUser.PasswordVerificationLockedUntil = until,
            maxAttempts,
            lockedUntil);

        await _context.SaveChangesAsync(cancellationToken);
        return trackedUser.PasswordVerificationLockedUntil;
    }

    private async Task ResetPasswordVerificationFailuresAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (_context.Database.IsRelational())
        {
            await _context.AppUsers
                .Where(candidate => candidate.Id == userId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.PasswordVerificationFailedAttempts, 0)
                    .SetProperty(candidate => candidate.PasswordVerificationLockedUntil, (DateTime?)null),
                    cancellationToken);
            return;
        }

        var trackedUser = await _context.AppUsers
            .SingleAsync(candidate => candidate.Id == userId, cancellationToken);
        trackedUser.PasswordVerificationFailedAttempts = 0;
        trackedUser.PasswordVerificationLockedUntil = null;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<DateTime?> RecordSecurityQuestionRecoveryFailureAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, MaxSecurityQuestionRecoveryAttempts);
        var now = DateTime.UtcNow;
        var lockedUntil = now.AddMinutes(Math.Max(1, SecurityQuestionRecoveryLockoutMinutes));

        if (_context.Database.IsRelational())
        {
            await _context.AppUsers
                .Where(candidate => candidate.Id == userId &&
                    (!candidate.SecurityQuestionRecoveryLockedUntil.HasValue ||
                     candidate.SecurityQuestionRecoveryLockedUntil.Value <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        candidate => candidate.SecurityQuestionRecoveryLockedUntil,
                        candidate => candidate.SecurityQuestionRecoveryFailedAttempts + 1 >= maxAttempts
                            ? lockedUntil
                            : null)
                    .SetProperty(
                        candidate => candidate.SecurityQuestionRecoveryFailedAttempts,
                        candidate => candidate.SecurityQuestionRecoveryFailedAttempts + 1 >= maxAttempts
                            ? 0
                            : candidate.SecurityQuestionRecoveryFailedAttempts + 1),
                    cancellationToken);

            return await _context.AppUsers
                .AsNoTracking()
                .Where(candidate => candidate.Id == userId)
                .Select(candidate => candidate.SecurityQuestionRecoveryLockedUntil)
                .SingleAsync(cancellationToken);
        }

        var trackedUser = await _context.AppUsers.SingleAsync(candidate => candidate.Id == userId, cancellationToken);
        if (trackedUser.SecurityQuestionRecoveryLockedUntil.HasValue &&
            trackedUser.SecurityQuestionRecoveryLockedUntil.Value <= now)
        {
            trackedUser.SecurityQuestionRecoveryLockedUntil = null;
            trackedUser.SecurityQuestionRecoveryFailedAttempts = 0;
        }
        RegisterFailureInMemory(
            () => trackedUser.SecurityQuestionRecoveryFailedAttempts,
            attempts => trackedUser.SecurityQuestionRecoveryFailedAttempts = attempts,
            until => trackedUser.SecurityQuestionRecoveryLockedUntil = until,
            maxAttempts,
            lockedUntil);
        await _context.SaveChangesAsync(cancellationToken);
        return trackedUser.SecurityQuestionRecoveryLockedUntil;
    }

    private static ObjectResult SecurityQuestionRecoveryLockedResult(DateTime lockedUntil)
    {
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((lockedUntil - DateTime.UtcNow).TotalSeconds));
        return new ObjectResult(new
        {
            message = "Too many recovery attempts. Please try again later.",
            retryAfterSeconds
        }) { StatusCode = StatusCodes.Status429TooManyRequests };
    }

    private static OkObjectResult PasswordVerificationLockedResult(DateTime lockedUntil)
    {
        var retryAfterSeconds = Math.Max(
            1,
            (int)Math.Ceiling((lockedUntil - DateTime.UtcNow).TotalSeconds));
        var minutesLeft = Math.Max(1, (int)Math.Ceiling(retryAfterSeconds / 60d));
        return new OkObjectResult(new
        {
            verified = false,
            locked = true,
            retryAfterSeconds,
            message = $"Too many incorrect password attempts. Try again in {minutesLeft} minute(s)."
        });
    }

    private async Task<IActionResult?> VerifyPasswordForSensitiveActionAsync(
        AppUser user,
        string? password,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (user.PasswordVerificationLockedUntil.HasValue &&
            user.PasswordVerificationLockedUntil.Value > now)
        {
            return SensitiveActionPasswordLockedResult(user.PasswordVerificationLockedUntil.Value);
        }

        var result = _passwordHasher.VerifyHashedPassword(
            user.Username,
            user.PasswordHash,
            password ?? string.Empty);
        if (result == PasswordVerificationResult.Failed)
        {
            var lockedUntil = await RecordPasswordVerificationFailureAsync(user.Id, cancellationToken);
            return lockedUntil.HasValue && lockedUntil.Value > now
                ? SensitiveActionPasswordLockedResult(lockedUntil.Value)
                : new BadRequestObjectResult(new { message = "Incorrect password." });
        }

        await ResetPasswordVerificationFailuresAsync(user.Id, cancellationToken);
        return null;
    }

    private static ObjectResult SensitiveActionPasswordLockedResult(DateTime lockedUntil)
    {
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((lockedUntil - DateTime.UtcNow).TotalSeconds));
        return new ObjectResult(new
        {
            message = "Too many incorrect password attempts. Please try again later.",
            retryAfterSeconds
        }) { StatusCode = StatusCodes.Status429TooManyRequests };
    }

    private async Task<bool> TryConsumePendingTwoFactorAsync(
        PendingTwoFactor pending,
        CancellationToken cancellationToken)
    {
        if (_context.Database.IsRelational())
        {
            return await _context.PendingTwoFactors
                .Where(candidate => candidate.Id == pending.Id && candidate.ExpiresAt >= DateTime.UtcNow)
                .ExecuteDeleteAsync(cancellationToken) == 1;
        }

        if (_context.Entry(pending).State == EntityState.Deleted) return false;
        _context.PendingTwoFactors.Remove(pending);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> TryClaimTotpTimeStepAsync(
        AppUser user,
        long timeStep,
        CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational())
        {
            if (user.LastTotpTimeStep.HasValue && user.LastTotpTimeStep.Value >= timeStep)
            {
                return false;
            }

            user.LastTotpTimeStep = timeStep;
            return true;
        }

        var claimed = await _context.AppUsers
            .Where(u => u.Id == user.Id && (!u.LastTotpTimeStep.HasValue || u.LastTotpTimeStep.Value < timeStep))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(u => u.LastTotpTimeStep, timeStep),
                cancellationToken);
        if (claimed == 1)
        {
            user.LastTotpTimeStep = timeStep;
            return true;
        }

        return false;
    }

    private async Task SweepExpiredPendingTwoFactorsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var expired = await _context.PendingTwoFactors
            .Where(p => p.UserId == userId && p.ExpiresAt < DateTime.UtcNow)
            .ToListAsync(cancellationToken);
        _context.PendingTwoFactors.RemoveRange(expired);
    }

    private async Task RemovePendingTwoFactorsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var pending = await _context.PendingTwoFactors
            .Where(p => p.UserId == userId)
            .ToListAsync(cancellationToken);
        _context.PendingTwoFactors.RemoveRange(pending);
    }
}
