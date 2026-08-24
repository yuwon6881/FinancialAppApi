using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class AuthAccountService
{
    private const int PendingTwoFactorTtlMinutes = 5;
    private const int MaxCodeAttempts = 5;

    private readonly AppDbContext _context;
    private readonly PasswordHasher<string> _passwordHasher = new();
    private readonly IConfiguration _configuration;
    private readonly TotpService _totpService;
    private readonly SecretProtector _secretProtector;
    private readonly RecoveryCodeService _recoveryCodeService;
    private readonly AuthSessionService _authSessionService;
    private readonly FinancialClock _financialClock;

    public AuthAccountService(
        AppDbContext context,
        IConfiguration configuration,
        TotpService totpService,
        SecretProtector secretProtector,
        RecoveryCodeService recoveryCodeService,
        AuthSessionService authSessionService,
        FinancialClock? financialClock = null)
    {
        _context = context;
        _configuration = configuration;
        _totpService = totpService;
        _secretProtector = secretProtector;
        _recoveryCodeService = recoveryCodeService;
        _authSessionService = authSessionService;
        _financialClock = financialClock ?? FinancialClock.Utc;
    }

    private int MaxFailedLoginAttempts => _configuration.GetValue("Auth:MaxFailedLoginAttempts", 5);
    private int LockoutMinutes => _configuration.GetValue("Auth:LockoutMinutes", 15);
    private int MaxPasswordVerificationAttempts =>
        _configuration.GetValue("Auth:MaxPasswordVerificationAttempts", MaxFailedLoginAttempts);
    private int PasswordVerificationLockoutMinutes =>
        _configuration.GetValue("Auth:PasswordVerificationLockoutMinutes", LockoutMinutes);
    private int MaxTwoFactorAttempts => _configuration.GetValue("Auth:MaxTwoFactorAttempts", MaxCodeAttempts);
    private int TwoFactorLockoutMinutes => _configuration.GetValue("Auth:TwoFactorLockoutMinutes", LockoutMinutes);
    private int MaxSecurityQuestionRecoveryAttempts =>
        _configuration.GetValue("Auth:MaxSecurityQuestionRecoveryAttempts", MaxFailedLoginAttempts);
    private int SecurityQuestionRecoveryLockoutMinutes =>
        _configuration.GetValue("Auth:SecurityQuestionRecoveryLockoutMinutes", LockoutMinutes);
    public const int MinimumPasswordLength = 8;
    public const int MaximumPasswordLength = 128;

    private static string? ValidateNewPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return "Password is required.";
        if (password.Length < MinimumPasswordLength)
            return $"Password must be at least {MinimumPasswordLength} characters.";
        if (password.Length > MaximumPasswordLength)
            return $"Password must be {MaximumPasswordLength} characters or fewer.";
        return null;
    }

    /// <summary>
    /// How many accounts may exist. Defaults to 1 (single-user). The legacy
    /// <c>Auth:AllowAdditionalUsers=true</c> flag is honoured as "unlimited" for back-compat.
    /// </summary>
    private int MaxUsers =>
        _configuration.GetValue("Auth:AllowAdditionalUsers", false)
            ? int.MaxValue
            : Math.Max(1, _configuration.GetValue("Auth:MaxUsers", 1));

    private async Task<bool> IsRegistrationOpenAsync(CancellationToken cancellationToken) =>
        await _context.AppUsers.CountAsync(cancellationToken) < MaxUsers;

    public async Task<IActionResult> GetStatusAsync(
        string? username = null,
        string? deviceCredentialId = null,
        CancellationToken cancellationToken = default)
    {
        var userCount = await _context.AppUsers.CountAsync(cancellationToken);
        var hasUser = userCount > 0;
        var hasFingerprint = string.IsNullOrWhiteSpace(username)
            ? hasUser && await _context.WebAuthnCredentials.AnyAsync(cancellationToken)
            : await _context.AppUsers
                .Where(user => user.NormalizedUsername == username.Trim().ToUpperInvariant())
                .AnyAsync(
                    user => _context.WebAuthnCredentials.Any(credential => credential.UserId == user.Id),
                    cancellationToken);
        var hasFingerprintOnDevice = false;
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(deviceCredentialId))
        {
            if (string.Equals(deviceCredentialId, "already_enrolled", StringComparison.Ordinal))
            {
                // The browser records this account-scoped marker only after the platform
                // authenticator rejects registration because an excluded account credential
                // already exists on this device.
                hasFingerprintOnDevice = hasFingerprint;
            }
            else
            {
                try
                {
                    var credentialId = Convert.FromHexString(deviceCredentialId);
                    var normalizedUsername = username.Trim().ToUpperInvariant();
                    hasFingerprintOnDevice = await _context.AppUsers
                        .Where(user => user.NormalizedUsername == normalizedUsername)
                        .AnyAsync(
                            user => _context.WebAuthnCredentials.Any(credential =>
                                credential.UserId == user.Id && credential.CredentialId == credentialId),
                            cancellationToken);
                }
                catch (FormatException)
                {
                    // A malformed local browser marker is simply not a registered device.
                }
            }
        }
        // registrationOpen lets the login screen offer a signup form to additional invitees
        // (up to Auth:MaxUsers) even after the first account exists.
        return new OkObjectResult(new
        {
            isRegistered = hasUser,
            hasFingerprint,
            hasFingerprintOnDevice,
            registrationOpen = userCount < MaxUsers,
        });
    }

    public async Task<IActionResult> RegisterAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return new BadRequestObjectResult(new { message = "Username and password are required." });
        }
        var passwordError = ValidateNewPassword(password);
        if (passwordError != null) return new BadRequestObjectResult(new { message = passwordError });

        var slot = await AllocateRegistrationSlotAsync(cancellationToken);
        if (slot is null && MaxUsers != int.MaxValue)
        {
            return new BadRequestObjectResult(new { message = "Registration is closed. The user limit has been reached." });
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Username = username.Trim(),
            NormalizedUsername = username.Trim().ToUpperInvariant(),
            // A distinct slot in [1..MaxUsers]; its unique index makes the cap race-safe
            // (two concurrent registrations for the last slot collide, and one is rejected).
            RegistrationSlot = slot
        };
        user.PasswordHash = _passwordHasher.HashPassword(user.Username, password);

        _context.SetCurrentUser(user.Id);
        _context.AppUsers.Add(user);
        DbSeeder.EnsureUserDefaults(_context, user.Id, _financialClock);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            if (await _context.AppUsers.AnyAsync(existing =>
                    existing.NormalizedUsername == user.NormalizedUsername,
                    cancellationToken))
            {
                return new BadRequestObjectResult(new { message = "That username is already registered." });
            }

            // A concurrent registration may have claimed the slot we picked; if the cap is now
            // full, report it as closed rather than surfacing a raw persistence error.
            if (!await IsRegistrationOpenAsync(cancellationToken))
            {
                return new BadRequestObjectResult(new { message = "Registration is closed. The user limit has been reached." });
            }

            throw;
        }

        return new OkObjectResult(new { message = "Registration successful" });
    }

    /// <summary>
    /// Returns the lowest free registration slot in [1..MaxUsers], or null when the cap is full
    /// (or, in unlimited mode, always null — no slot is tracked). Reuses slots freed by deleted
    /// accounts so the limit reflects the live user count, not the high-water mark.
    /// </summary>
    private async Task<int?> AllocateRegistrationSlotAsync(CancellationToken cancellationToken)
    {
        var maxUsers = MaxUsers;
        if (maxUsers == int.MaxValue)
        {
            return null;
        }

        var usedSlots = await _context.AppUsers
            .Where(u => u.RegistrationSlot != null)
            .Select(u => u.RegistrationSlot!.Value)
            .ToListAsync(cancellationToken);
        var used = usedSlots.ToHashSet();

        for (var candidate = 1; candidate <= maxUsers; candidate++)
        {
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<IActionResult> LoginAsync(
        string username,
        string password,
        string? deviceId,
        string? deviceName,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new BadRequestObjectResult(new { message = "Username and password are required." });
        }

        var normalizedUsername = username.Trim().ToUpperInvariant();
        var user = await _context.AppUsers
            .FirstOrDefaultAsync(u => u.NormalizedUsername == normalizedUsername, cancellationToken);
        if (user == null)
        {
            return new UnauthorizedObjectResult(new { message = "Invalid username or password" });
        }
        _context.SetCurrentUser(user.Id);

        if (user.LockedUntil.HasValue && user.LockedUntil.Value > DateTime.UtcNow)
        {
            var minutesLeft = Math.Ceiling((user.LockedUntil.Value - DateTime.UtcNow).TotalMinutes);
            return new ObjectResult(new { message = $"Too many failed attempts. Try again in {minutesLeft} minute(s)." }) { StatusCode = 429 };
        }

        var result = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            await RecordPasswordFailureAsync(user, cancellationToken);
            return new UnauthorizedObjectResult(new { message = "Invalid username or password" });
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;

        if (user.TotpEnabled)
        {
            await SweepExpiredPendingTwoFactorsAsync(user.Id, cancellationToken);
            if (user.TwoFactorLockedUntil.HasValue && user.TwoFactorLockedUntil.Value > DateTime.UtcNow)
            {
                var minutesLeft = Math.Ceiling((user.TwoFactorLockedUntil.Value - DateTime.UtcNow).TotalMinutes);
                await _context.SaveChangesAsync(cancellationToken);
                return new ObjectResult(new { message = $"Too many two-factor attempts. Try again in {minutesLeft} minute(s)." }) { StatusCode = 429 };
            }

            var existingPending = await _context.PendingTwoFactors
                .Where(p => p.UserId == user.Id)
                .ToListAsync(cancellationToken);
            _context.PendingTwoFactors.RemoveRange(existingPending);

            var pending = new PendingTwoFactor
            {
                UserId = user.Id,
                Username = user.Username,
                DeviceId = deviceId,
                DeviceName = deviceName,
                ExpiresAt = DateTime.UtcNow.AddMinutes(PendingTwoFactorTtlMinutes)
            };
            _context.PendingTwoFactors.Add(pending);
            await _context.SaveChangesAsync(cancellationToken);
            return new OkObjectResult(new { requiresTwoFactor = true, pendingToken = pending.Id.ToString() });
        }

        await _context.SaveChangesAsync(cancellationToken);
        var session = await _authSessionService.CreateSessionAsync(
            user,
            deviceId,
            deviceName,
            ipAddress,
            userAgent,
            cancellationToken: cancellationToken);
        return new OkObjectResult(new { token = session.Token, username = user.Username, hasSetupSecurityQuestions = user.HasSetupSecurityQuestions });
    }

    public async Task<IActionResult> LoginTwoFactorAsync(
        string pendingToken,
        string code,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pendingToken) || !Guid.TryParse(pendingToken, out var pendingId))
        {
            return new UnauthorizedObjectResult(new { message = "Login session expired. Please log in again." });
        }

        var pending = await _context.PendingTwoFactors
            .FirstOrDefaultAsync(p => p.Id == pendingId, cancellationToken);
        if (pending == null || pending.ExpiresAt < DateTime.UtcNow)
        {
            if (pending != null)
            {
                _context.PendingTwoFactors.Remove(pending);
                await _context.SaveChangesAsync(cancellationToken);
            }
            return new UnauthorizedObjectResult(new { message = "Login session expired. Please log in again." });
        }

        if (pending.Attempts >= MaxCodeAttempts)
        {
            _context.PendingTwoFactors.Remove(pending);
            await _context.SaveChangesAsync(cancellationToken);
            return new UnauthorizedObjectResult(new { message = "Too many attempts. Please log in again." });
        }

        var user = await _context.AppUsers
            .FirstOrDefaultAsync(u => u.Id == pending.UserId, cancellationToken);
        if (user == null || !user.TotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
        {
            _context.PendingTwoFactors.Remove(pending);
            await _context.SaveChangesAsync(cancellationToken);
            return new UnauthorizedObjectResult(new { message = "Login session expired. Please log in again." });
        }
        _context.SetCurrentUser(user.Id);

        if (user.TwoFactorLockedUntil.HasValue && user.TwoFactorLockedUntil.Value > DateTime.UtcNow)
        {
            await RemovePendingTwoFactorsAsync(user.Id, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            var minutesLeft = Math.Ceiling((user.TwoFactorLockedUntil.Value - DateTime.UtcNow).TotalMinutes);
            return new ObjectResult(new { message = $"Too many two-factor attempts. Try again in {minutesLeft} minute(s)." }) { StatusCode = 429 };
        }

        var secret = _secretProtector.Unprotect(user.TotpSecret);
        var validTotp = _totpService.ValidateCode(secret, code, out var timeStepMatched);
        var validRecovery = !validTotp && await _recoveryCodeService.TryConsumeAsync(
            user.Username,
            code,
            user.Id,
            cancellationToken);

        if (!validTotp && !validRecovery)
        {
            return await RecordTwoFactorFailureAsync(user, pending, cancellationToken: cancellationToken);
        }

        if (validTotp && !await TryClaimTotpTimeStepAsync(user, timeStepMatched, cancellationToken))
        {
            return await RecordTwoFactorFailureAsync(
                user,
                pending,
                "Code has already been used",
                cancellationToken);
        }

        if (!await TryConsumePendingTwoFactorAsync(pending, cancellationToken))
        {
            return new UnauthorizedObjectResult(new { message = "Login session expired. Please log in again." });
        }

        user.TwoFactorFailedAttempts = 0;
        user.TwoFactorLockedUntil = null;
        await RemovePendingTwoFactorsAsync(user.Id, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        var session = await _authSessionService.CreateSessionAsync(
            user,
            pending.DeviceId,
            pending.DeviceName,
            ipAddress,
            userAgent,
            cancellationToken: cancellationToken);
        return new OkObjectResult(new { token = session.Token, username = user.Username, hasSetupSecurityQuestions = user.HasSetupSecurityQuestions });
    }

    public async Task<IActionResult> VerifyPasswordAsync(
        string? username,
        string password,
        string? currentToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return new BadRequestObjectResult(new { message = "Password is required" });
        }
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedObjectResult(new { message = "User not found in session" });
        }

        var user = await _context.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.NormalizedUsername == username.Trim().ToUpperInvariant(),
                cancellationToken);
        if (user == null)
        {
            return new UnauthorizedObjectResult(new { message = "User not found" });
        }

        if (user.PasswordVerificationLockedUntil.HasValue &&
            user.PasswordVerificationLockedUntil.Value > DateTime.UtcNow)
        {
            return PasswordVerificationLockedResult(user.PasswordVerificationLockedUntil.Value);
        }

        var result = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            var lockedUntil = await RecordPasswordVerificationFailureAsync(
                user.Id,
                cancellationToken);
            if (lockedUntil.HasValue && lockedUntil.Value > DateTime.UtcNow)
            {
                return PasswordVerificationLockedResult(lockedUntil.Value);
            }

            return new OkObjectResult(new { verified = false, message = "Incorrect password" });
        }

        await ResetPasswordVerificationFailuresAsync(user.Id, cancellationToken);
        await _authSessionService.UnlockSessionAsync(currentToken, cancellationToken);
        return new OkObjectResult(new { verified = true });
    }

    public async Task<IActionResult> ChangePasswordAsync(
        string? username,
        string currentPassword,
        string newPassword,
        string? currentToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword))
        {
            return new BadRequestObjectResult(new { message = "Current and new password are required." });
        }
        var passwordError = ValidateNewPassword(newPassword);
        if (passwordError != null) return new BadRequestObjectResult(new { message = passwordError });
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

        var passwordFailure = await VerifyPasswordForSensitiveActionAsync(user, currentPassword, cancellationToken);
        if (passwordFailure != null) return passwordFailure;

        user.PasswordHash = _passwordHasher.HashPassword(user.Username, newPassword);
        var revokedOtherSessions = await _authSessionService.RevokeOtherSessionsAsync(
            username,
            currentToken,
            cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new OkObjectResult(new { message = "Password changed successfully.", revokedOtherSessions });
    }



}

public class QuestionAnswerDto
{
    public int QuestionId { get; set; }
    public string Answer { get; set; } = string.Empty;
}
