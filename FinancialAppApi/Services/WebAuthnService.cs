using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public class WebAuthnService
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _context;
    private readonly IConfiguration _config;
    private readonly AuthSessionService _authSessionService;

    public WebAuthnService(AppDbContext context, IConfiguration config, AuthSessionService authSessionService)
    {
        _context = context;
        _config = config;
        _authSessionService = authSessionService;
    }

    public async Task<IActionResult> RegisterOptionsAsync(string? username, string? requestOrigin, string fallbackOrigin)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedObjectResult(new { message = "User not found in session" });
        }

        await CleanupExpiredChallengesAsync();
        var userId = _context.RequireCurrentUserId();

        var existingCredentialIds = await _context.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .Select(c => c.CredentialId)
            .ToListAsync();

        var fido2 = BuildFido2(requestOrigin, fallbackOrigin);
        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                DisplayName = username,
                Name = username,
                Id = Encoding.UTF8.GetBytes(username)
            },
            ExcludeCredentials = existingCredentialIds.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                AuthenticatorAttachment = AuthenticatorAttachment.Platform,
                ResidentKey = ResidentKeyRequirement.Discouraged,
                UserVerification = UserVerificationRequirement.Required
            },
            AttestationPreference = AttestationConveyancePreference.None
        });

        var challengeId = Guid.NewGuid().ToString("N");
        _context.WebAuthnChallenges.Add(new WebAuthnChallenge
        {
            Id = challengeId,
            UserId = userId,
            Purpose = "register",
            Username = username,
            OptionsJson = options.ToJson(),
            ExpiresAt = DateTime.UtcNow.Add(ChallengeLifetime)
        });
        await _context.SaveChangesAsync();

        return new OkObjectResult(new { challengeId, options });
    }

    public async Task<IActionResult> RegisterVerifyAsync(
        string? username,
        string challengeId,
        AuthenticatorAttestationRawResponse credential,
        string? deviceLabel,
        string? requestOrigin,
        string fallbackOrigin)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedObjectResult(new { message = "User not found in session" });
        }

        var userId = _context.RequireCurrentUserId();
        var challenge = await ClaimChallengeAsync(challengeId, "register", userId);
        if (challenge == null)
        {
            return new BadRequestObjectResult(new { message = "Registration challenge expired or invalid. Please try again." });
        }

        var options = CredentialCreateOptions.FromJson(challenge.OptionsJson);
        var fido2 = BuildFido2(requestOrigin, fallbackOrigin);

        IsCredentialIdUniqueToUserAsyncDelegate callback = async (args, cancellationToken) =>
            !await _context.WebAuthnCredentials.AnyAsync(c => c.CredentialId == args.CredentialId, cancellationToken);

        dynamic result;
        try
        {
            result = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = credential,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = callback
            });
        }
        catch (Exception e)
        {
            return new BadRequestObjectResult(new { message = "Device unlock setup failed: " + e.Message });
        }

        _context.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            CredentialId = result.Id,
            UserId = userId,
            Username = username,
            PublicKey = result.PublicKey,
            SignCount = result.SignCount,
            DeviceLabel = deviceLabel,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        return new OkObjectResult(new { message = "Device unlock set up successfully." });
    }

    public async Task<IActionResult> LoginOptionsAsync(
        string? requestOrigin,
        string fallbackOrigin,
        string? username = null)
    {
        await CleanupExpiredChallengesAsync();

        if (!await _context.AppUsers.AnyAsync())
        {
            return new BadRequestObjectResult(new { message = "No account registered." });
        }

        var usersWithCredentials = _context.AppUsers
            .Where(user => _context.WebAuthnCredentials.Any(credential => credential.UserId == user.Id));
        List<AppUser> candidates;
        if (string.IsNullOrWhiteSpace(username))
        {
            candidates = await usersWithCredentials.OrderBy(user => user.Id).Take(2).ToListAsync();
            if (candidates.Count > 1)
            {
                return new BadRequestObjectResult(new
                {
                    message = "Enter a username before using device unlock."
                });
            }
        }
        else
        {
            var normalizedUsername = username.Trim().ToUpperInvariant();
            candidates = await usersWithCredentials
                .Where(user => user.NormalizedUsername == normalizedUsername)
                .Take(1)
                .ToListAsync();
        }

        var user = candidates.SingleOrDefault();
        if (user == null)
        {
            return new BadRequestObjectResult(new { message = "Device unlock is not set up yet." });
        }

        var credentials = await _context.WebAuthnCredentials
            .Where(c => c.UserId == user.Id)
            .Select(c => c.CredentialId)
            .ToListAsync();

        var fido2 = BuildFido2(requestOrigin, fallbackOrigin);
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentials.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            UserVerification = UserVerificationRequirement.Required
        });

        var challengeId = Guid.NewGuid().ToString("N");
        _context.WebAuthnChallenges.Add(new WebAuthnChallenge
        {
            Id = challengeId,
            UserId = user.Id,
            Purpose = "login",
            Username = user.Username,
            OptionsJson = options.ToJson(),
            ExpiresAt = DateTime.UtcNow.Add(ChallengeLifetime)
        });
        await _context.SaveChangesAsync();

        return new OkObjectResult(new { challengeId, options });
    }

    public async Task<IActionResult> LoginVerifyAsync(
        string challengeId,
        AuthenticatorAssertionRawResponse credential,
        string? deviceId,
        string? deviceName,
        string? ipAddress,
        string? userAgent,
        string? requestOrigin,
        string fallbackOrigin)
    {
        var challenge = await ClaimChallengeAsync(challengeId, "login");
        if (challenge == null)
        {
            return new UnauthorizedObjectResult(new { message = "Login challenge expired or invalid. Please try again." });
        }

        var storedCred = await _context.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == credential.RawId);
        var user = storedCred == null
            ? null
            : await _context.AppUsers.FirstOrDefaultAsync(u => u.Id == storedCred.UserId);
        if (storedCred == null || user == null ||
            !string.Equals(challenge.UserId, user.Id, StringComparison.Ordinal))
        {
            return new UnauthorizedObjectResult(new { message = "Unrecognized device credential." });
        }

        var verifyResult = await VerifyAssertionAsync(storedCred, credential, challenge.OptionsJson, requestOrigin, fallbackOrigin);
        if (verifyResult != null)
        {
            return verifyResult;
        }

        var session = await _authSessionService.CreateSessionAsync(
            user,
            deviceId,
            deviceName,
            ipAddress,
            userAgent,
            storedCred.CredentialId);

        await _context.SaveChangesAsync();

        return new OkObjectResult(new { token = session.Token, username = storedCred.Username, hasSetupSecurityQuestions = user.HasSetupSecurityQuestions });
    }

    public async Task<IActionResult> AssertOptionsAsync(string? username, string? requestOrigin, string fallbackOrigin)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedObjectResult(new { message = "User not found in session" });
        }

        await CleanupExpiredChallengesAsync();
        var userId = _context.RequireCurrentUserId();

        var credentials = await _context.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .Select(c => c.CredentialId)
            .ToListAsync();
        if (credentials.Count == 0)
        {
            return new BadRequestObjectResult(new { message = "Device unlock is not set up yet." });
        }

        var fido2 = BuildFido2(requestOrigin, fallbackOrigin);
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentials.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            UserVerification = UserVerificationRequirement.Required
        });

        var challengeId = Guid.NewGuid().ToString("N");
        _context.WebAuthnChallenges.Add(new WebAuthnChallenge
        {
            Id = challengeId,
            UserId = userId,
            Purpose = "assert",
            Username = username,
            OptionsJson = options.ToJson(),
            ExpiresAt = DateTime.UtcNow.Add(ChallengeLifetime)
        });
        await _context.SaveChangesAsync();

        return new OkObjectResult(new { challengeId, options });
    }

    public async Task<IActionResult> AssertVerifyAsync(
        string? username,
        string challengeId,
        AuthenticatorAssertionRawResponse credential,
        string? currentToken,
        string? requestOrigin,
        string fallbackOrigin)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedObjectResult(new { message = "User not found in session" });
        }

        var userId = _context.RequireCurrentUserId();
        var challenge = await ClaimChallengeAsync(challengeId, "assert", userId);
        if (challenge == null)
        {
            return new UnauthorizedObjectResult(new { message = "Verification challenge expired or invalid. Please try again." });
        }

        var storedCred = await _context.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == credential.RawId && c.UserId == userId);
        if (storedCred == null)
        {
            return new UnauthorizedObjectResult(new { message = "Unrecognized device credential." });
        }

        var verifyResult = await VerifyAssertionAsync(storedCred, credential, challenge.OptionsJson, requestOrigin, fallbackOrigin);
        if (verifyResult != null)
        {
            return verifyResult;
        }

        await _authSessionService.UnlockSessionAsync(currentToken);
        await _context.SaveChangesAsync();

        return new OkObjectResult(new { verified = true });
    }

    public async Task<IActionResult> ListCredentialsAsync(string? username)
    {
        var userId = _context.RequireCurrentUserId();
        var creds = await _context.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { id = Convert.ToHexString(c.CredentialId), deviceLabel = c.DeviceLabel, createdAt = c.CreatedAt })
            .ToListAsync();
        return new OkObjectResult(creds);
    }

    public async Task<IActionResult> DeleteCredentialAsync(string? username, string id)
    {
        byte[] credentialId;
        try
        {
            credentialId = Convert.FromHexString(id);
        }
        catch (FormatException)
        {
            return new BadRequestObjectResult(new { message = "Invalid credential id." });
        }

        var userId = _context.RequireCurrentUserId();
        var cred = await _context.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == credentialId && c.UserId == userId);
        if (cred == null)
        {
            return new NotFoundObjectResult(new { message = "Credential not found." });
        }

        _context.WebAuthnCredentials.Remove(cred);
        await _context.SaveChangesAsync();
        return new OkObjectResult(new { message = "Device unlock credential removed." });
    }

    private async Task<IActionResult?> VerifyAssertionAsync(
        WebAuthnCredential storedCred,
        AuthenticatorAssertionRawResponse credential,
        string optionsJson,
        string? requestOrigin,
        string fallbackOrigin)
    {
        var options = AssertionOptions.FromJson(optionsJson);
        var fido2 = BuildFido2(requestOrigin, fallbackOrigin);
        var expectedUserHandle = Encoding.UTF8.GetBytes(storedCred.Username);

        IsUserHandleOwnerOfCredentialIdAsync callback = (args, cancellationToken) =>
            Task.FromResult(args.UserHandle.SequenceEqual(expectedUserHandle));

        dynamic result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = credential,
                OriginalOptions = options,
                StoredPublicKey = storedCred.PublicKey,
                StoredSignatureCounter = (uint)storedCred.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = callback
            });
        }
        catch (Exception e)
        {
            var message = e.Message.Contains("User Verified flag not set", StringComparison.OrdinalIgnoreCase)
                ? "This device did not verify your identity. Use its PIN, fingerprint, face recognition, or screen lock, then try again."
                : "Device verification failed: " + e.Message;
            return new UnauthorizedObjectResult(new { message });
        }

        storedCred.SignCount = result.SignCount;
        return null;
    }

    private Fido2 BuildFido2(string? requestOrigin, string fallbackOrigin)
    {
        var configuredOrigins = _config.GetSection("WebAuthn:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

        HashSet<string> origins;
        if (configuredOrigins.Length > 0)
        {
            origins = configuredOrigins.ToHashSet(StringComparer.Ordinal);
        }
        else if (!string.IsNullOrEmpty(requestOrigin))
        {
            origins = new HashSet<string>(StringComparer.Ordinal) { requestOrigin };
        }
        else
        {
            origins = new HashSet<string>(StringComparer.Ordinal) { fallbackOrigin };
        }

        var configuredRpId = _config["WebAuthn:RpId"];
        var rpId = !string.IsNullOrWhiteSpace(configuredRpId)
            ? configuredRpId.Trim()
            : origins.Select(origin => new Uri(origin).Host)
                .OrderBy(host => host, StringComparer.Ordinal)
                .First();

        return new Fido2(new Fido2Configuration
        {
            ServerDomain = rpId,
            ServerName = "FinancialApp Ledger",
            Origins = origins
        });
    }

    private async Task<WebAuthnChallenge?> ClaimChallengeAsync(
        string challengeId,
        string purpose,
        string? userId = null)
    {
        var now = DateTime.UtcNow;
        var query = _context.WebAuthnChallenges
            .Where(challenge => challenge.Id == challengeId && challenge.Purpose == purpose);
        if (userId != null)
        {
            query = query.Where(challenge => challenge.UserId == userId);
        }

        var challenge = await query.AsNoTracking().FirstOrDefaultAsync();
        if (challenge == null || challenge.ExpiresAt < now) return null;

        if (_context.Database.IsRelational())
        {
            var claimed = await query
                .Where(candidate => candidate.ExpiresAt >= now)
                .ExecuteDeleteAsync();
            return claimed == 1 ? challenge : null;
        }

        _context.WebAuthnChallenges.Remove(challenge);
        await _context.SaveChangesAsync();
        return challenge;
    }

    private async Task CleanupExpiredChallengesAsync()
    {
        var userId = _context.CurrentUserId;
        var expired = await _context.WebAuthnChallenges
            .Where(c => c.ExpiresAt < DateTime.UtcNow && (userId == null || c.UserId == userId))
            .ToListAsync();
        if (expired.Count > 0)
        {
            _context.WebAuthnChallenges.RemoveRange(expired);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException exception)
            {
                foreach (var entry in exception.Entries.Where(e => e.State == EntityState.Deleted))
                    entry.State = EntityState.Detached;
            }
        }
    }
}
