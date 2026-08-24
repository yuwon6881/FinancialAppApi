using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class WebAuthnService
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _context;
    private readonly IConfiguration _config;
    private readonly AuthSessionService _authSessionService;
    private readonly ILogger<WebAuthnService>? _logger;

    public WebAuthnService(
        AppDbContext context,
        IConfiguration config,
        AuthSessionService authSessionService,
        ILogger<WebAuthnService>? logger = null)
    {
        _context = context;
        _config = config;
        _authSessionService = authSessionService;
        _logger = logger;
    }

    public async Task<IActionResult> RegisterOptionsAsync(
        string? username,
        string? requestOrigin,
        string fallbackOrigin,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedObjectResult(new { message = "User not found in session" });
        }

        await CleanupExpiredChallengesAsync(cancellationToken);
        var userId = _context.RequireCurrentUserId();

        var existingCredentialIds = await _context.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .Select(c => c.CredentialId)
            .ToListAsync(cancellationToken);

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
        await _context.SaveChangesAsync(cancellationToken);

        return new OkObjectResult(new { challengeId, options });
    }

    public async Task<IActionResult> RegisterVerifyAsync(
        string? username,
        string challengeId,
        AuthenticatorAttestationRawResponse credential,
        string? deviceLabel,
        string? requestOrigin,
        string fallbackOrigin,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(username))
        {
            return new UnauthorizedObjectResult(new { message = "User not found in session" });
        }

        var userId = _context.RequireCurrentUserId();
        var challenge = await ClaimChallengeAsync(
            challengeId,
            "register",
            userId,
            cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "WebAuthn credential registration failed for user {UserId}.", userId);
            return new BadRequestObjectResult(new { message = "Device unlock setup failed. Please try again." });
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
        await _context.SaveChangesAsync(cancellationToken);

        return new OkObjectResult(new { message = "Device unlock set up successfully." });
    }

    public async Task<IActionResult> LoginOptionsAsync(
        string? requestOrigin,
        string fallbackOrigin,
        string? username = null,
        CancellationToken cancellationToken = default)
    {
        await CleanupExpiredChallengesAsync(cancellationToken);

        if (!await _context.AppUsers.AnyAsync(cancellationToken))
        {
            return new BadRequestObjectResult(new { message = "No account registered." });
        }

        var usersWithCredentials = _context.AppUsers
            .Where(user => _context.WebAuthnCredentials.Any(credential => credential.UserId == user.Id));
        List<AppUser> candidates;
        if (string.IsNullOrWhiteSpace(username))
        {
            candidates = await usersWithCredentials
                .OrderBy(user => user.Id)
                .Take(2)
                .ToListAsync(cancellationToken);
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
                .ToListAsync(cancellationToken);
        }

        var user = candidates.SingleOrDefault();
        if (user == null)
        {
            return new BadRequestObjectResult(new { message = "Device unlock is not set up yet." });
        }

        var credentials = await _context.WebAuthnCredentials
            .Where(c => c.UserId == user.Id)
            .Select(c => c.CredentialId)
            .ToListAsync(cancellationToken);

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
        await _context.SaveChangesAsync(cancellationToken);

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
        string fallbackOrigin,
        CancellationToken cancellationToken = default)
    {
        var challenge = await ClaimChallengeAsync(
            challengeId,
            "login",
            cancellationToken: cancellationToken);
        if (challenge == null)
        {
            return new UnauthorizedObjectResult(new { message = "Login challenge expired or invalid. Please try again." });
        }

        var storedCred = await _context.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == credential.RawId, cancellationToken);
        var user = storedCred == null
            ? null
            : await _context.AppUsers.FirstOrDefaultAsync(
                u => u.Id == storedCred.UserId,
                cancellationToken);
        if (storedCred == null || user == null ||
            !string.Equals(challenge.UserId, user.Id, StringComparison.Ordinal))
        {
            return new UnauthorizedObjectResult(new { message = "Unrecognized device credential." });
        }

        var verifyResult = await VerifyAssertionAsync(
            storedCred,
            credential,
            challenge.OptionsJson,
            requestOrigin,
            fallbackOrigin,
            cancellationToken);
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
            storedCred.CredentialId,
            cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return new OkObjectResult(new { token = session.Token, username = storedCred.Username, hasSetupSecurityQuestions = user.HasSetupSecurityQuestions });
    }

}
