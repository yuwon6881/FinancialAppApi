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

        var existingCredentialIds = await _context.WebAuthnCredentials
            .Where(c => c.Username == username)
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

        var challenge = await _context.WebAuthnChallenges
            .FirstOrDefaultAsync(c => c.Id == challengeId && c.Purpose == "register" && c.Username == username);
        if (challenge == null || challenge.ExpiresAt < DateTime.UtcNow)
        {
            return new BadRequestObjectResult(new { message = "Registration challenge expired or invalid. Please try again." });
        }
        _context.WebAuthnChallenges.Remove(challenge);

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
            await _context.SaveChangesAsync();
            return new BadRequestObjectResult(new { message = "Fingerprint registration failed: " + e.Message });
        }

        _context.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            CredentialId = result.Id,
            Username = username,
            PublicKey = result.PublicKey,
            SignCount = result.SignCount,
            DeviceLabel = deviceLabel,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        return new OkObjectResult(new { message = "Fingerprint registered successfully." });
    }

    public async Task<IActionResult> LoginOptionsAsync(string? requestOrigin, string fallbackOrigin)
    {
        await CleanupExpiredChallengesAsync();

        var user = await _context.AppUsers.FirstOrDefaultAsync();
        if (user == null)
        {
            return new BadRequestObjectResult(new { message = "No account registered." });
        }

        var credentials = await _context.WebAuthnCredentials
            .Where(c => c.Username == user.Username)
            .Select(c => c.CredentialId)
            .ToListAsync();
        if (credentials.Count == 0)
        {
            return new BadRequestObjectResult(new { message = "Fingerprint login is not set up yet." });
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
        var challenge = await _context.WebAuthnChallenges
            .FirstOrDefaultAsync(c => c.Id == challengeId && c.Purpose == "login");
        if (challenge == null || challenge.ExpiresAt < DateTime.UtcNow)
        {
            return new UnauthorizedObjectResult(new { message = "Login challenge expired or invalid. Please try again." });
        }
        _context.WebAuthnChallenges.Remove(challenge);

        var storedCred = await _context.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == credential.RawId);
        var user = storedCred == null
            ? null
            : await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == storedCred.Username);
        if (storedCred == null || user == null || !string.Equals(challenge.Username, user.Username, StringComparison.Ordinal))
        {
            await _context.SaveChangesAsync();
            return new UnauthorizedObjectResult(new { message = "Unrecognized fingerprint credential." });
        }

        var verifyResult = await VerifyAssertionAsync(storedCred, credential, challenge.OptionsJson, requestOrigin, fallbackOrigin);
        if (verifyResult != null)
        {
            await _context.SaveChangesAsync();
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

        return new OkObjectResult(new { token = session.Token, username = storedCred.Username });
    }

    public async Task<IActionResult> AssertOptionsAsync(string? username, string? requestOrigin, string fallbackOrigin)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedObjectResult(new { message = "User not found in session" });
        }

        await CleanupExpiredChallengesAsync();

        var credentials = await _context.WebAuthnCredentials
            .Where(c => c.Username == username)
            .Select(c => c.CredentialId)
            .ToListAsync();
        if (credentials.Count == 0)
        {
            return new BadRequestObjectResult(new { message = "Fingerprint login is not set up yet." });
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

        var challenge = await _context.WebAuthnChallenges
            .FirstOrDefaultAsync(c => c.Id == challengeId && c.Purpose == "assert" && c.Username == username);
        if (challenge == null || challenge.ExpiresAt < DateTime.UtcNow)
        {
            return new UnauthorizedObjectResult(new { message = "Verification challenge expired or invalid. Please try again." });
        }
        _context.WebAuthnChallenges.Remove(challenge);

        var storedCred = await _context.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == credential.RawId && c.Username == username);
        if (storedCred == null)
        {
            await _context.SaveChangesAsync();
            return new UnauthorizedObjectResult(new { message = "Unrecognized fingerprint credential." });
        }

        var verifyResult = await VerifyAssertionAsync(storedCred, credential, challenge.OptionsJson, requestOrigin, fallbackOrigin);
        if (verifyResult != null)
        {
            await _context.SaveChangesAsync();
            return verifyResult;
        }

        await _authSessionService.UnlockSessionAsync(currentToken);
        await _context.SaveChangesAsync();

        return new OkObjectResult(new { verified = true });
    }

    public async Task<IActionResult> ListCredentialsAsync(string? username)
    {
        var creds = await _context.WebAuthnCredentials
            .Where(c => c.Username == username)
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

        var cred = await _context.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == credentialId && c.Username == username);
        if (cred == null)
        {
            return new NotFoundObjectResult(new { message = "Credential not found." });
        }

        _context.WebAuthnCredentials.Remove(cred);
        await _context.SaveChangesAsync();
        return new OkObjectResult(new { message = "Fingerprint credential removed." });
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
            return new UnauthorizedObjectResult(new { message = "Fingerprint verification failed: " + e.Message });
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

    private async Task CleanupExpiredChallengesAsync()
    {
        var expired = await _context.WebAuthnChallenges.Where(c => c.ExpiresAt < DateTime.UtcNow).ToListAsync();
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
