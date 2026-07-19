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

    /// <summary>
    /// How many accounts may exist. Defaults to 1 (single-user). The legacy
    /// <c>Auth:AllowAdditionalUsers=true</c> flag is honoured as "unlimited" for back-compat.
    /// </summary>
    private int MaxUsers =>
        _configuration.GetValue("Auth:AllowAdditionalUsers", false)
            ? int.MaxValue
            : Math.Max(1, _configuration.GetValue("Auth:MaxUsers", 1));

    private async Task<bool> IsRegistrationOpenAsync() =>
        await _context.AppUsers.CountAsync() < MaxUsers;

    public async Task<IActionResult> GetStatusAsync(string? username = null)
    {
        var userCount = await _context.AppUsers.CountAsync();
        var hasUser = userCount > 0;
        var hasFingerprint = string.IsNullOrWhiteSpace(username)
            ? hasUser && await _context.WebAuthnCredentials.AnyAsync()
            : await _context.AppUsers
                .Where(user => user.NormalizedUsername == username.Trim().ToUpperInvariant())
                .AnyAsync(user => _context.WebAuthnCredentials.Any(credential => credential.UserId == user.Id));
        // registrationOpen lets the login screen offer a signup form to additional invitees
        // (up to Auth:MaxUsers) even after the first account exists.
        return new OkObjectResult(new
        {
            isRegistered = hasUser,
            hasFingerprint,
            registrationOpen = userCount < MaxUsers,
        });
    }

    public async Task<IActionResult> RegisterAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new BadRequestObjectResult(new { message = "Username and password are required." });
        }

        var slot = await AllocateRegistrationSlotAsync();
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
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            if (await _context.AppUsers.AnyAsync(existing =>
                    existing.NormalizedUsername == user.NormalizedUsername))
            {
                return new BadRequestObjectResult(new { message = "That username is already registered." });
            }

            // A concurrent registration may have claimed the slot we picked; if the cap is now
            // full, report it as closed rather than surfacing a raw persistence error.
            if (!await IsRegistrationOpenAsync())
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
    private async Task<int?> AllocateRegistrationSlotAsync()
    {
        var maxUsers = MaxUsers;
        if (maxUsers == int.MaxValue)
        {
            return null;
        }

        var usedSlots = await _context.AppUsers
            .Where(u => u.RegistrationSlot != null)
            .Select(u => u.RegistrationSlot!.Value)
            .ToListAsync();
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
        string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new BadRequestObjectResult(new { message = "Username and password are required." });
        }

        var normalizedUsername = username.Trim().ToUpperInvariant();
        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.NormalizedUsername == normalizedUsername);
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
            await RecordPasswordFailureAsync(user);
            return new UnauthorizedObjectResult(new { message = "Invalid username or password" });
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;

        if (user.TotpEnabled)
        {
            await SweepExpiredPendingTwoFactorsAsync(user.Id);
            if (user.TwoFactorLockedUntil.HasValue && user.TwoFactorLockedUntil.Value > DateTime.UtcNow)
            {
                var minutesLeft = Math.Ceiling((user.TwoFactorLockedUntil.Value - DateTime.UtcNow).TotalMinutes);
                await _context.SaveChangesAsync();
                return new ObjectResult(new { message = $"Too many two-factor attempts. Try again in {minutesLeft} minute(s)." }) { StatusCode = 429 };
            }

            var existingPending = await _context.PendingTwoFactors
                .Where(p => p.UserId == user.Id)
                .ToListAsync();
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
            await _context.SaveChangesAsync();
            return new OkObjectResult(new { requiresTwoFactor = true, pendingToken = pending.Id.ToString() });
        }

        await _context.SaveChangesAsync();
        var session = await _authSessionService.CreateSessionAsync(user, deviceId, deviceName, ipAddress, userAgent);
        return new OkObjectResult(new { token = session.Token, username = user.Username, hasSetupSecurityQuestions = user.HasSetupSecurityQuestions });
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

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Id == pending.UserId);
        if (user == null || !user.TotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
        {
            _context.PendingTwoFactors.Remove(pending);
            await _context.SaveChangesAsync();
            return new UnauthorizedObjectResult(new { message = "Login session expired. Please log in again." });
        }
        _context.SetCurrentUser(user.Id);

        if (user.TwoFactorLockedUntil.HasValue && user.TwoFactorLockedUntil.Value > DateTime.UtcNow)
        {
            await RemovePendingTwoFactorsAsync(user.Id);
            await _context.SaveChangesAsync();
            var minutesLeft = Math.Ceiling((user.TwoFactorLockedUntil.Value - DateTime.UtcNow).TotalMinutes);
            return new ObjectResult(new { message = $"Too many two-factor attempts. Try again in {minutesLeft} minute(s)." }) { StatusCode = 429 };
        }

        var secret = _secretProtector.Unprotect(user.TotpSecret);
        var validTotp = _totpService.ValidateCode(secret, code, out var timeStepMatched);
        var validRecovery = !validTotp && await _recoveryCodeService.TryConsumeAsync(user.Username, code, user.Id);

        if (!validTotp && !validRecovery)
        {
            return await RecordTwoFactorFailureAsync(user, pending);
        }

        if (validTotp && !await TryClaimTotpTimeStepAsync(user, timeStepMatched))
        {
            return await RecordTwoFactorFailureAsync(user, pending, "Code has already been used");
        }

        if (!await TryConsumePendingTwoFactorAsync(pending))
        {
            return new UnauthorizedObjectResult(new { message = "Login session expired. Please log in again." });
        }

        user.TwoFactorFailedAttempts = 0;
        user.TwoFactorLockedUntil = null;
        await RemovePendingTwoFactorsAsync(user.Id);
        await _context.SaveChangesAsync();

        var session = await _authSessionService.CreateSessionAsync(user, pending.DeviceId, pending.DeviceName, ipAddress, userAgent);
        return new OkObjectResult(new { token = session.Token, username = user.Username, hasSetupSecurityQuestions = user.HasSetupSecurityQuestions });
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

        var user = await _context.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.NormalizedUsername == username.Trim().ToUpperInvariant());
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
            var lockedUntil = await RecordPasswordVerificationFailureAsync(user.Id);
            if (lockedUntil.HasValue && lockedUntil.Value > DateTime.UtcNow)
            {
                return PasswordVerificationLockedResult(lockedUntil.Value);
            }

            return new OkObjectResult(new { verified = false, message = "Incorrect password" });
        }

        await ResetPasswordVerificationFailuresAsync(user.Id);
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

        var recoveryCodes = await _recoveryCodeService.RegenerateAsync(username, user.Id);
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
        var validRecovery = !validTotp && await _recoveryCodeService.TryConsumeAsync(username, code ?? string.Empty, user.Id);
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
        await _recoveryCodeService.DeleteAllAsync(username, user.Id);

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

        var codes = await _recoveryCodeService.RegenerateAsync(username, user.Id);
        return new OkObjectResult(new { recoveryCodes = codes });
    }

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
        string message = "Invalid code")
    {
        if (_context.Database.IsRelational())
        {
            var maxAttempts = Math.Max(1, MaxTwoFactorAttempts);
            var now = DateTime.UtcNow;
            var lockedUntil = now.AddMinutes(TwoFactorLockoutMinutes);

            await _context.PendingTwoFactors
                .Where(candidate => candidate.Id == pending.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.Attempts, candidate => candidate.Attempts + 1));

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
                            : candidate.TwoFactorFailedAttempts + 1));

            var lockState = await _context.AppUsers
                .AsNoTracking()
                .Where(candidate => candidate.Id == user.Id)
                .Select(candidate => candidate.TwoFactorLockedUntil)
                .SingleAsync();
            if (lockState.HasValue && lockState.Value > now)
            {
                await _context.PendingTwoFactors
                    .Where(candidate => candidate.UserId == user.Id)
                    .ExecuteDeleteAsync();
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
            await RemovePendingTwoFactorsAsync(user.Id);
            await _context.SaveChangesAsync();
            return new ObjectResult(new { message = "Too many two-factor attempts. Please try again later." }) { StatusCode = 429 };
        }

        await _context.SaveChangesAsync();
        return new UnauthorizedObjectResult(new { message });
    }

    private async Task RecordPasswordFailureAsync(AppUser user)
    {
        if (!_context.Database.IsRelational())
        {
            RegisterFailureInMemory(
                () => user.FailedLoginAttempts,
                attempts => user.FailedLoginAttempts = attempts,
                lockedUntil => user.LockedUntil = lockedUntil,
                Math.Max(1, MaxFailedLoginAttempts),
                DateTime.UtcNow.AddMinutes(LockoutMinutes));
            await _context.SaveChangesAsync();
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
                        : candidate.FailedLoginAttempts + 1));
    }

    private async Task<DateTime?> RecordPasswordVerificationFailureAsync(string userId)
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
                            : candidate.PasswordVerificationFailedAttempts + 1));

            return await _context.AppUsers
                .AsNoTracking()
                .Where(candidate => candidate.Id == userId)
                .Select(candidate => candidate.PasswordVerificationLockedUntil)
                .SingleAsync();
        }

        var trackedUser = await _context.AppUsers.SingleAsync(candidate => candidate.Id == userId);
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

        await _context.SaveChangesAsync();
        return trackedUser.PasswordVerificationLockedUntil;
    }

    private async Task ResetPasswordVerificationFailuresAsync(string userId)
    {
        if (_context.Database.IsRelational())
        {
            await _context.AppUsers
                .Where(candidate => candidate.Id == userId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.PasswordVerificationFailedAttempts, 0)
                    .SetProperty(candidate => candidate.PasswordVerificationLockedUntil, (DateTime?)null));
            return;
        }

        var trackedUser = await _context.AppUsers.SingleAsync(candidate => candidate.Id == userId);
        trackedUser.PasswordVerificationFailedAttempts = 0;
        trackedUser.PasswordVerificationLockedUntil = null;
        await _context.SaveChangesAsync();
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

    private async Task<bool> TryConsumePendingTwoFactorAsync(PendingTwoFactor pending)
    {
        if (_context.Database.IsRelational())
        {
            return await _context.PendingTwoFactors
                .Where(candidate => candidate.Id == pending.Id && candidate.ExpiresAt >= DateTime.UtcNow)
                .ExecuteDeleteAsync() == 1;
        }

        if (_context.Entry(pending).State == EntityState.Deleted) return false;
        _context.PendingTwoFactors.Remove(pending);
        await _context.SaveChangesAsync();
        return true;
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

    private async Task SweepExpiredPendingTwoFactorsAsync(string userId)
    {
        var expired = await _context.PendingTwoFactors
            .Where(p => p.UserId == userId && p.ExpiresAt < DateTime.UtcNow)
            .ToListAsync();
        _context.PendingTwoFactors.RemoveRange(expired);
    }

    private async Task RemovePendingTwoFactorsAsync(string userId)
    {
        var pending = await _context.PendingTwoFactors
            .Where(p => p.UserId == userId)
            .ToListAsync();
        _context.PendingTwoFactors.RemoveRange(pending);
    }

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

    public async Task<IActionResult> GetSecurityQuestionsSetupStatusAsync(string? username)
    {
        if (string.IsNullOrEmpty(username)) return new UnauthorizedResult();
        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return new UnauthorizedResult();
        return new OkObjectResult(new { hasSetupSecurityQuestions = user.HasSetupSecurityQuestions });
    }

    public async Task<IActionResult> SetupSecurityQuestionsAsync(string? username, List<QuestionAnswerDto> answers)
    {
        if (string.IsNullOrEmpty(username)) return new UnauthorizedResult();
        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
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
        await _context.SaveChangesAsync();

        return new OkObjectResult(new { message = "Security questions configured successfully." });
    }

    public async Task<IActionResult> GetSecurityQuestionsForRecoveryAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return new BadRequestObjectResult(new { message = "Username is required." });

        var normalizedUsername = username.Trim().ToUpperInvariant();
        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.NormalizedUsername == normalizedUsername);

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
            .ToListAsync();

        var available = GetAvailableSecurityQuestions();
        var questionsToAsk = userQuestions.Select(id => new { QuestionId = id, Question = available[id] }).ToList();

        return new OkObjectResult(new { username = user.Username, questions = questionsToAsk });
    }

    public async Task<IActionResult> VerifySecurityQuestionsAndResetPasswordAsync(string username, List<QuestionAnswerDto> answers, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(newPassword))
        {
            return new BadRequestObjectResult(new { message = "Username and new password are required." });
        }

        var normalizedUsername = username.Trim().ToUpperInvariant();
        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.NormalizedUsername == normalizedUsername);

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
            .ToListAsync();

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

            await _context.SaveChangesAsync();
            await _authSessionService.RevokeAllSessionsAsync(user.Username, null, keepCurrent: false);

            return new OkObjectResult(new { message = "Password reset successfully." });
        }

        return new BadRequestObjectResult(new { message = "Answers are incorrect." });
    }
}

public class QuestionAnswerDto
{
    public int QuestionId { get; set; }
    public string Answer { get; set; } = string.Empty;
}
