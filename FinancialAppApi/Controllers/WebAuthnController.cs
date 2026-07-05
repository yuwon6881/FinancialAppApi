using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Extensions;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/auth/webauthn")]
public class WebAuthnController : ControllerBase
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _context;
    private readonly IConfiguration _config;

    public WebAuthnController(AppDbContext context, IConfiguration config)
    {
        _context = context;
        _config = config;
    }

    // Builds a per-request Fido2 instance so the relying party ID/origin always
    // matches wherever the frontend is actually hosted, rather than a domain
    // hardcoded at deploy time. Set WebAuthn:AllowedOrigins in appsettings to
    // lock this down to specific origins in production.
    private Fido2 BuildFido2()
    {
        var configuredOrigins = _config.GetSection("WebAuthn:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        var requestOrigin = Request.Headers.Origin.ToString();

        HashSet<string> origins;
        if (configuredOrigins.Length > 0)
        {
            origins = configuredOrigins.ToHashSet();
        }
        else if (!string.IsNullOrEmpty(requestOrigin))
        {
            origins = new HashSet<string> { requestOrigin };
        }
        else
        {
            origins = new HashSet<string> { $"{Request.Scheme}://{Request.Host}" };
        }

        return new Fido2(new Fido2Configuration
        {
            ServerDomain = new Uri(origins.First()).Host,
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
            await _context.SaveChangesAsync();
        }
    }

    // POST api/auth/webauthn/register/options
    // Enrolling a fingerprint requires an existing password-authenticated session.
    [AuthorizeToken]
    [HttpPost("register/options")]
    public async Task<IActionResult> RegisterOptions()
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized(new { message = "User not found in session" });
        }

        await CleanupExpiredChallengesAsync();

        var existingCredentialIds = await _context.WebAuthnCredentials
            .Where(c => c.Username == username)
            .Select(c => c.CredentialId)
            .ToListAsync();

        var fido2 = BuildFido2();
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

        return Ok(new { challengeId, options });
    }

    public class RegisterVerifyRequest
    {
        public string ChallengeId { get; set; } = string.Empty;
        public AuthenticatorAttestationRawResponse Credential { get; set; } = null!;
        public string? DeviceLabel { get; set; }
    }

    // POST api/auth/webauthn/register/verify
    [AuthorizeToken]
    [HttpPost("register/verify")]
    public async Task<IActionResult> RegisterVerify([FromBody] RegisterVerifyRequest request)
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized(new { message = "User not found in session" });
        }

        var challenge = await _context.WebAuthnChallenges
            .FirstOrDefaultAsync(c => c.Id == request.ChallengeId && c.Purpose == "register" && c.Username == username);
        if (challenge == null || challenge.ExpiresAt < DateTime.UtcNow)
        {
            return BadRequest(new { message = "Registration challenge expired or invalid. Please try again." });
        }
        _context.WebAuthnChallenges.Remove(challenge);

        var options = CredentialCreateOptions.FromJson(challenge.OptionsJson);
        var fido2 = BuildFido2();

        IsCredentialIdUniqueToUserAsyncDelegate callback = async (args, cancellationToken) =>
            !await _context.WebAuthnCredentials.AnyAsync(c => c.CredentialId == args.CredentialId, cancellationToken);

        dynamic result;
        try
        {
            result = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = request.Credential,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = callback
            });
        }
        catch (Exception e)
        {
            await _context.SaveChangesAsync();
            return BadRequest(new { message = "Fingerprint registration failed: " + e.Message });
        }

        _context.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            CredentialId = result.Id,
            Username = username,
            PublicKey = result.PublicKey,
            SignCount = result.SignCount,
            DeviceLabel = request.DeviceLabel,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        return Ok(new { message = "Fingerprint registered successfully." });
    }

    // POST api/auth/webauthn/login/options
    // Unauthenticated: this is how a session is obtained. Single-user app, so
    // there's no username to ask for - we just look up the one registered user.
    [HttpPost("login/options")]
    public async Task<IActionResult> LoginOptions()
    {
        await CleanupExpiredChallengesAsync();

        var user = await _context.AppUsers.FirstOrDefaultAsync();
        if (user == null)
        {
            return BadRequest(new { message = "No account registered." });
        }

        var credentials = await _context.WebAuthnCredentials
            .Where(c => c.Username == user.Username)
            .Select(c => c.CredentialId)
            .ToListAsync();
        if (credentials.Count == 0)
        {
            return BadRequest(new { message = "Fingerprint login is not set up yet." });
        }

        var fido2 = BuildFido2();
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

        return Ok(new { challengeId, options });
    }

    public class LoginVerifyRequest
    {
        public string ChallengeId { get; set; } = string.Empty;
        public AuthenticatorAssertionRawResponse Credential { get; set; } = null!;
    }

    // POST api/auth/webauthn/login/verify
    [HttpPost("login/verify")]
    public async Task<IActionResult> LoginVerify([FromBody] LoginVerifyRequest request)
    {
        var challenge = await _context.WebAuthnChallenges
            .FirstOrDefaultAsync(c => c.Id == request.ChallengeId && c.Purpose == "login");
        if (challenge == null || challenge.ExpiresAt < DateTime.UtcNow)
        {
            return Unauthorized(new { message = "Login challenge expired or invalid. Please try again." });
        }
        _context.WebAuthnChallenges.Remove(challenge);

        var storedCred = await _context.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == request.Credential.RawId);
        if (storedCred == null)
        {
            await _context.SaveChangesAsync();
            return Unauthorized(new { message = "Unrecognized fingerprint credential." });
        }

        var options = AssertionOptions.FromJson(challenge.OptionsJson);
        var fido2 = BuildFido2();
        var expectedUserHandle = Encoding.UTF8.GetBytes(storedCred.Username);

        IsUserHandleOwnerOfCredentialIdAsync callback = (args, cancellationToken) =>
            Task.FromResult(args.UserHandle.SequenceEqual(expectedUserHandle));

        dynamic result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = request.Credential,
                OriginalOptions = options,
                StoredPublicKey = storedCred.PublicKey,
                StoredSignatureCounter = (uint)storedCred.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = callback
            });
        }
        catch (Exception e)
        {
            await _context.SaveChangesAsync();
            return Unauthorized(new { message = "Fingerprint verification failed: " + e.Message });
        }

        storedCred.SignCount = result.SignCount;

        var expiredSessions = await _context.UserSessions.Where(s => s.ExpiresAt < DateTime.UtcNow).ToListAsync();
        if (expiredSessions.Count > 0)
        {
            _context.UserSessions.RemoveRange(expiredSessions);
        }

        // A WebAuthn credential is enrolled per-device, so it doubles as a
        // device identifier: replace whatever session this same credential
        // last issued instead of leaving it to linger (e.g. the device's
        // local storage was cleared/reinstalled, so it still had a valid,
        // now-unreachable session from before).
        var priorSessionsForCredential = await _context.UserSessions
            .Where(s => s.CredentialId != null && s.CredentialId == storedCred.CredentialId)
            .ToListAsync();
        if (priorSessionsForCredential.Count > 0)
        {
            _context.UserSessions.RemoveRange(priorSessionsForCredential);
        }

        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        _context.UserSessions.Add(new UserSession
        {
            Token = token,
            Username = storedCred.Username,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CredentialId = storedCred.CredentialId
        });

        await _context.SaveChangesAsync();

        return Ok(new { token, username = storedCred.Username });
    }

    // POST api/auth/webauthn/assert/options
    // Authenticated re-verification (unlocking the lock screen, or proving
    // identity to reveal sensitive figures) -- unlike login/options, this
    // never issues a session, it just proves "it's still you" for a session
    // that already exists.
    [AuthorizeToken]
    [HttpPost("assert/options")]
    public async Task<IActionResult> AssertOptions()
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized(new { message = "User not found in session" });
        }

        await CleanupExpiredChallengesAsync();

        var credentials = await _context.WebAuthnCredentials
            .Where(c => c.Username == username)
            .Select(c => c.CredentialId)
            .ToListAsync();
        if (credentials.Count == 0)
        {
            return BadRequest(new { message = "Fingerprint login is not set up yet." });
        }

        var fido2 = BuildFido2();
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

        return Ok(new { challengeId, options });
    }

    public class AssertVerifyRequest
    {
        public string ChallengeId { get; set; } = string.Empty;
        public AuthenticatorAssertionRawResponse Credential { get; set; } = null!;
    }

    // POST api/auth/webauthn/assert/verify
    // Mirrors verify-password: proves identity against the CALLER'S EXISTING
    // session (unlocking it if locked) and never creates a new UserSession row.
    [AuthorizeToken]
    [HttpPost("assert/verify")]
    public async Task<IActionResult> AssertVerify([FromBody] AssertVerifyRequest request)
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized(new { message = "User not found in session" });
        }

        var challenge = await _context.WebAuthnChallenges
            .FirstOrDefaultAsync(c => c.Id == request.ChallengeId && c.Purpose == "assert" && c.Username == username);
        if (challenge == null || challenge.ExpiresAt < DateTime.UtcNow)
        {
            return Unauthorized(new { message = "Verification challenge expired or invalid. Please try again." });
        }
        _context.WebAuthnChallenges.Remove(challenge);

        var storedCred = await _context.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == request.Credential.RawId && c.Username == username);
        if (storedCred == null)
        {
            await _context.SaveChangesAsync();
            return Unauthorized(new { message = "Unrecognized fingerprint credential." });
        }

        var options = AssertionOptions.FromJson(challenge.OptionsJson);
        var fido2 = BuildFido2();
        var expectedUserHandle = Encoding.UTF8.GetBytes(storedCred.Username);

        IsUserHandleOwnerOfCredentialIdAsync callback = (args, cancellationToken) =>
            Task.FromResult(args.UserHandle.SequenceEqual(expectedUserHandle));

        dynamic result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = request.Credential,
                OriginalOptions = options,
                StoredPublicKey = storedCred.PublicKey,
                StoredSignatureCounter = (uint)storedCred.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = callback
            });
        }
        catch (Exception e)
        {
            await _context.SaveChangesAsync();
            return Unauthorized(new { message = "Fingerprint verification failed: " + e.Message });
        }

        storedCred.SignCount = result.SignCount;

        // Unlock the caller's existing session if it was locked (same effect
        // as verify-password) -- no session row is created or replaced here.
        if (Request.TryGetBearerToken(out var token) == BearerTokenResult.Ok)
        {
            var session = await _context.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
            if (session != null)
            {
                session.IsLocked = false;
            }
        }

        await _context.SaveChangesAsync();

        return Ok(new { verified = true });
    }

    // GET api/auth/webauthn/credentials
    [AuthorizeToken]
    [HttpGet("credentials")]
    public async Task<IActionResult> ListCredentials()
    {
        var username = HttpContext.Items["Username"] as string;
        var creds = await _context.WebAuthnCredentials
            .Where(c => c.Username == username)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { id = Convert.ToHexString(c.CredentialId), deviceLabel = c.DeviceLabel, createdAt = c.CreatedAt })
            .ToListAsync();
        return Ok(creds);
    }

    // DELETE api/auth/webauthn/credentials/{id}
    [AuthorizeToken]
    [HttpDelete("credentials/{id}")]
    public async Task<IActionResult> DeleteCredential(string id)
    {
        var username = HttpContext.Items["Username"] as string;
        byte[] credentialId;
        try
        {
            credentialId = Convert.FromHexString(id);
        }
        catch (FormatException)
        {
            return BadRequest(new { message = "Invalid credential id." });
        }

        var cred = await _context.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == credentialId && c.Username == username);
        if (cred == null)
        {
            return NotFound(new { message = "Credential not found." });
        }

        _context.WebAuthnCredentials.Remove(cred);
        await _context.SaveChangesAsync();
        return Ok(new { message = "Fingerprint credential removed." });
    }
}
