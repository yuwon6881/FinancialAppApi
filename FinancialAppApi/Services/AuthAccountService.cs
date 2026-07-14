using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public class AuthAccountService
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

    public AuthAccountService(
        AppDbContext context,
        IConfiguration configuration,
        TotpService totpService,
        SecretProtector secretProtector,
        RecoveryCodeService recoveryCodeService,
        AuthSessionService authSessionService)
    {
        _context = context;
        _configuration = configuration;
        _totpService = totpService;
        _secretProtector = secretProtector;
        _recoveryCodeService = recoveryCodeService;
        _authSessionService = authSessionService;
    }

    private int MaxFailedLoginAttempts => _configuration.GetValue("Auth:MaxFailedLoginAttempts", 5);
    private int LockoutMinutes => _configuration.GetValue("Auth:LockoutMinutes", 15);
    private int MaxTwoFactorAttempts => _configuration.GetValue("Auth:MaxTwoFactorAttempts", MaxCodeAttempts);
    private int TwoFactorLockoutMinutes => _configuration.GetValue("Auth:TwoFactorLockoutMinutes", LockoutMinutes);

    public async Task<IActionResult> GetStatusAsync()
    {
        var hasUser = await _context.AppUsers.AnyAsync();
        var hasFingerprint = hasUser && await _context.WebAuthnCredentials.AnyAsync();
        return new OkObjectResult(new { isRegistered = hasUser, hasFingerprint });
    }

    public async Task<IActionResult> RegisterAsync(string username, string password)
    {
        if (await _context.AppUsers.AnyAsync())
        {
            return new BadRequestObjectResult(new { message = "Registration is closed. A user is already registered." });
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new BadRequestObjectResult(new { message = "Username and password are required." });
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Username = username.Trim()
        };
        user.PasswordHash = _passwordHasher.HashPassword(user.Username, password);

        _context.AppUsers.Add(user);
        await _context.SaveChangesAsync();

        return new OkObjectResult(new { message = "Registration successful" });
    }

    public async Task<IActionResult> LoginAsync(
        string username,
        string password,
        string? deviceId,
        string? deviceName,
        string? ipAddress,
        string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new BadRequestObjectResult(new { message = "Username and password are required." });
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
        if (user == null)
        {
            return new UnauthorizedObjectResult(new { message = "Invalid username or password" });
        }

        if (user.LockedUntil.HasValue && user.LockedUntil.Value > DateTime.UtcNow)
        {
            var minutesLeft = Math.Ceiling((user.LockedUntil.Value - DateTime.UtcNow).TotalMinutes);
            return new ObjectResult(new { message = $"Too many failed attempts. Try again in {minutesLeft} minute(s)." }) { StatusCode = 429 };
        }

        var result = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            user.FailedLoginAttempts += 1;
            if (user.FailedLoginAttempts >= MaxFailedLoginAttempts)
            {
                user.LockedUntil = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                user.FailedLoginAttempts = 0;
            }
            await _context.SaveChangesAsync();
            return new UnauthorizedObjectResult(new { message = "Invalid username or password" });
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;

        if (user.TotpEnabled)
        {
            await SweepExpiredPendingTwoFactorsAsync();
            if (user.TwoFactorLockedUntil.HasValue && user.TwoFactorLockedUntil.Value > DateTime.UtcNow)
            {
                var minutesLeft = Math.Ceiling((user.TwoFactorLockedUntil.Value - DateTime.UtcNow).TotalMinutes);
                await _context.SaveChangesAsync();
                return new ObjectResult(new { message = $"Too many two-factor attempts. Try again in {minutesLeft} minute(s)." }) { StatusCode = 429 };
            }

            var existingPending = await _context.PendingTwoFactors
                .Where(p => p.Username == user.Username)
                .ToListAsync();
            _context.PendingTwoFactors.RemoveRange(existingPending);

            var pending = new PendingTwoFactor
            {
                Username = user.Username,
                DeviceId = deviceId,
                DeviceName = deviceName,
                ExpiresAt = DateTime.UtcNow.AddMinutes(PendingTwoFactorTtlMinutes)
            };
            _context.PendingTwoFactors.Add(pending);
            await _context.SaveChangesAsync();
            return new OkObjectResult(new { requiresTwoFactor = true, pendingToken = pending.Id.ToString() });
        }

        await _context.SaveChangesAsync();
        var session = await _authSessionService.CreateSessionAsync(user, deviceId, deviceName, ipAddress, userAgent);
        return new OkObjectResult(new { token = session.Token, username = user.Username });
    }

    public async Task<IActionResult> LoginTwoFactorAsync(
        string pendingToken,
        string code,
        string? ipAddress,
        string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(pendingToken) || !Guid.TryParse(pendingToken, out var pendingId))
        {
            return new UnauthorizedObjectResult(new { message = "Login session expired. Please log in again." });
        }

        var pending = await _context.PendingTwoFactors.FirstOrDefaultAsync(p => p.Id == pendingId);
        if (pending == null || pending.ExpiresAt < DateTime.UtcNow)
        {
            if (pending != null)
            {
                _context.PendingTwoFactors.Remove(pending);
                await _context.SaveChangesAsync();
            }
            return new UnauthorizedObjectResult(new { message = "Login session expired. Please log in again." });
        }

        if (pending.Attempts >= MaxCodeAttempts)
        {
            _context.PendingTwoFactors.Remove(pending);
            await _context.SaveChangesAsync();
            return new UnauthorizedObjectResult(new { message = "Too many attempts. Please log in again." });
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == pending.Username);
        if (user == null || !user.TotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
        {
            _context.PendingTwoFactors.Remove(pending);
            await _context.SaveChangesAsync();
            return new UnauthorizedObjectResult(new { message = "Login session expired. Please log in again." });
        }

        if (user.TwoFactorLockedUntil.HasValue && user.TwoFactorLockedUntil.Value > DateTime.UtcNow)
        {
            await RemovePendingTwoFactorsAsync(user.Username);
            await _context.SaveChangesAsync();
            var minutesLeft = Math.Ceiling((user.TwoFactorLockedUntil.Value - DateTime.UtcNow).TotalMinutes);
            return new ObjectResult(new { message = $"Too many two-factor attempts. Try again in {minutesLeft} minute(s)." }) { StatusCode = 429 };
        }

        var secret = _secretProtector.Unprotect(user.TotpSecret);
        var validTotp = _totpService.ValidateCode(secret, code, out var timeStepMatched);
        var validRecovery = !validTotp && await _recoveryCodeService.TryConsumeAsync(user.Username, code);

        if (!validTotp && !validRecovery)
        {
            return await RecordTwoFactorFailureAsync(user, pending);
        }

        if (validTotp && !await TryClaimTotpTimeStepAsync(user, timeStepMatched))
        {
            return await RecordTwoFactorFailureAsync(user, pending, "Code has already been used");
        }

        user.TwoFactorFailedAttempts = 0;
        user.TwoFactorLockedUntil = null;
        await RemovePendingTwoFactorsAsync(user.Username);
        await _context.SaveChangesAsync();

        var session = await _authSessionService.CreateSessionAsync(user, pending.DeviceId, pending.DeviceName, ipAddress, userAgent);
        return new OkObjectResult(new { token = session.Token, username = user.Username });
    }

    public async Task<IActionResult> VerifyPasswordAsync(string? username, string password, string? currentToken)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return new BadRequestObjectResult(new { message = "Password is required" });
        }
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedObjectResult(new { message = "User not found in session" });
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
        if (user == null)
        {
            return new UnauthorizedObjectResult(new { message = "User not found" });
        }

        var result = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return new OkObjectResult(new { verified = false, message = "Incorrect password" });
        }

        await _authSessionService.UnlockSessionAsync(currentToken);
        return new OkObjectResult(new { verified = true });
    }

    public async Task<IActionResult> ChangePasswordAsync(string? username, string currentPassword, string newPassword, string? currentToken)
    {
        if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword))
        {
            return new BadRequestObjectResult(new { message = "Current and new password are required." });
        }
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedResult();
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null)
        {
            return new UnauthorizedResult();
        }

        var result = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, currentPassword);
        if (result == PasswordVerificationResult.Failed)
        {
            return new BadRequestObjectResult(new { message = "Current password is incorrect." });
        }

        user.PasswordHash = _passwordHasher.HashPassword(user.Username, newPassword);
        var revokedOtherSessions = await _authSessionService.RevokeOtherSessionsAsync(username, currentToken);
        await _context.SaveChangesAsync();

        return new OkObjectResult(new { message = "Password changed successfully.", revokedOtherSessions });
    }

    public async Task<IActionResult> GetTwoFactorStatusAsync(string? username)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedResult();
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null)
        {
            return new UnauthorizedResult();
        }

        return new OkObjectResult(new { enabled = user.TotpEnabled });
    }

    public async Task<IActionResult> SetupTotpAsync(string? username)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedResult();
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
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
        await _context.SaveChangesAsync();

        return new OkObjectResult(new { secret, otpauthUri = _totpService.BuildOtpAuthUri(secret, username) });
    }

    public async Task<IActionResult> EnableTotpAsync(string? username, string? code, string? currentToken)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedResult();
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
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

        var recoveryCodes = await _recoveryCodeService.RegenerateAsync(username);
        await _authSessionService.RevokeOtherSessionsAsync(username, currentToken);
        await _context.SaveChangesAsync();

        return new OkObjectResult(new { enabled = true, recoveryCodes });
    }

    public async Task<IActionResult> DisableTotpAsync(string? username, string? password, string? code)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedResult();
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null || !user.TotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
        {
            return new BadRequestObjectResult(new { message = "Two-factor authentication is not enabled." });
        }

        var passwordResult = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, password ?? string.Empty);
        if (passwordResult == PasswordVerificationResult.Failed)
        {
            return new BadRequestObjectResult(new { message = "Incorrect password." });
        }

        var secret = _secretProtector.Unprotect(user.TotpSecret);
        var validTotp = _totpService.ValidateCode(secret, code ?? string.Empty);
        var validRecovery = !validTotp && await _recoveryCodeService.TryConsumeAsync(username, code ?? string.Empty);
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
        await _context.SaveChangesAsync();
        await _recoveryCodeService.DeleteAllAsync(username);

        return new OkObjectResult(new { message = "Two-factor authentication disabled." });
    }

    public async Task<IActionResult> RegenerateRecoveryCodesAsync(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedResult();
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null || !user.TotpEnabled)
        {
            return new BadRequestObjectResult(new { message = "Two-factor authentication is not enabled." });
        }

        var passwordResult = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, password ?? string.Empty);
        if (passwordResult == PasswordVerificationResult.Failed)
        {
            return new BadRequestObjectResult(new { message = "Incorrect password." });
        }

        var codes = await _recoveryCodeService.RegenerateAsync(username);
        return new OkObjectResult(new { recoveryCodes = codes });
    }

    private async Task<IActionResult> RecordTwoFactorFailureAsync(
        AppUser user,
        PendingTwoFactor pending,
        string message = "Invalid code")
    {
        pending.Attempts += 1;
        user.TwoFactorFailedAttempts += 1;
        if (user.TwoFactorFailedAttempts >= MaxTwoFactorAttempts)
        {
            user.TwoFactorLockedUntil = DateTime.UtcNow.AddMinutes(TwoFactorLockoutMinutes);
            user.TwoFactorFailedAttempts = 0;
            await RemovePendingTwoFactorsAsync(user.Username);
            await _context.SaveChangesAsync();
            return new ObjectResult(new { message = "Too many two-factor attempts. Please try again later." }) { StatusCode = 429 };
        }

        await _context.SaveChangesAsync();
        return new UnauthorizedObjectResult(new { message });
    }

    private async Task<bool> TryClaimTotpTimeStepAsync(AppUser user, long timeStep)
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
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.LastTotpTimeStep, timeStep));
        if (claimed == 1)
        {
            user.LastTotpTimeStep = timeStep;
            return true;
        }

        return false;
    }

    private async Task SweepExpiredPendingTwoFactorsAsync()
    {
        var expired = await _context.PendingTwoFactors
            .Where(p => p.ExpiresAt < DateTime.UtcNow)
            .ToListAsync();
        _context.PendingTwoFactors.RemoveRange(expired);
    }

    private async Task RemovePendingTwoFactorsAsync(string username)
    {
        var pending = await _context.PendingTwoFactors
            .Where(p => p.Username == username)
            .ToListAsync();
        _context.PendingTwoFactors.RemoveRange(pending);
    }
}
