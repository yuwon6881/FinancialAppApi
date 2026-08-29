using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using FinancialAppApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class WebAuthnService
{
    public async Task<IActionResult> AssertOptionsAsync(
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

        var credentials = await _context.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .Select(c => c.CredentialId)
            .ToListAsync(cancellationToken);
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
        await PersistChallengeAsync(new WebAuthnChallenge
        {
            Id = challengeId,
            UserId = userId,
            Purpose = "assert",
            Username = username,
            OptionsJson = options.ToJson(),
            ExpiresAt = DateTime.UtcNow.Add(ChallengeLifetime)
        }, cancellationToken);

        return new OkObjectResult(new { challengeId, options });
    }

    public async Task<IActionResult> AssertVerifyAsync(
        string? username,
        string challengeId,
        AuthenticatorAssertionRawResponse credential,
        string? currentToken,
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
            "assert",
            userId,
            cancellationToken);
        if (challenge == null)
        {
            return new UnauthorizedObjectResult(new { message = "Verification challenge expired or invalid. Please try again." });
        }

        var storedCred = await _context.WebAuthnCredentials
            .FirstOrDefaultAsync(
                c => c.CredentialId == credential.RawId && c.UserId == userId,
                cancellationToken);
        if (storedCred == null)
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

        await _authSessionService.UnlockSessionAsync(currentToken, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new OkObjectResult(new { verified = true });
    }

    public async Task<IActionResult> ListCredentialsAsync(
        string? username,
        CancellationToken cancellationToken = default)
    {
        var userId = _context.RequireCurrentUserId();
        var creds = await _context.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { id = Convert.ToHexString(c.CredentialId), deviceLabel = c.DeviceLabel, createdAt = c.CreatedAt })
            .ToListAsync(cancellationToken);
        return new OkObjectResult(creds);
    }

    public async Task<IActionResult> DeleteCredentialAsync(
        string? username,
        string id,
        CancellationToken cancellationToken = default)
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
            .FirstOrDefaultAsync(
                c => c.CredentialId == credentialId && c.UserId == userId,
                cancellationToken);
        if (cred == null)
        {
            return new NotFoundObjectResult(new { message = "Credential not found." });
        }

        _context.WebAuthnCredentials.Remove(cred);
        await _context.SaveChangesAsync(cancellationToken);
        return new OkObjectResult(new { message = "Device unlock credential removed." });
    }

    private async Task<IActionResult?> VerifyAssertionAsync(
        WebAuthnCredential storedCred,
        AuthenticatorAssertionRawResponse credential,
        string optionsJson,
        string? requestOrigin,
        string fallbackOrigin,
        CancellationToken cancellationToken)
    {
        var options = AssertionOptions.FromJson(optionsJson);
        var fido2 = BuildFido2(requestOrigin, fallbackOrigin);
        var expectedUserHandle = Encoding.UTF8.GetBytes(storedCred.Username);

        IsUserHandleOwnerOfCredentialIdAsync callback = (args, cancellationToken) =>
            Task.FromResult(args.UserHandle.SequenceEqual(expectedUserHandle));

        dynamic result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = credential,
                OriginalOptions = options,
                StoredPublicKey = storedCred.PublicKey,
                StoredSignatureCounter = (uint)storedCred.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = callback
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "WebAuthn assertion failed for credential {CredentialId}.", Convert.ToHexString(storedCred.CredentialId));
            var message = exception.Message.Contains("User Verified flag not set", StringComparison.OrdinalIgnoreCase)
                ? "This device did not verify your identity. Use its PIN, fingerprint, face recognition, or screen lock, then try again."
                : "Device verification failed. Please try again or use your password.";
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

    internal async Task PersistChallengeAsync(
        WebAuthnChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        _context.WebAuthnChallenges.Add(challenge);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // EnableRetryOnFailure can replay SaveChanges when PostgreSQL committed the insert but
            // the acknowledgement was lost. The replay then reports a duplicate primary key even
            // though this exact challenge is already durable. Accept only that exact committed
            // payload; a real collision or a different database failure must still surface.
            _context.Entry(challenge).State = EntityState.Detached;
            var alreadyCommitted = await _context.WebAuthnChallenges
                .AsNoTracking()
                .AnyAsync(candidate =>
                    candidate.Id == challenge.Id &&
                    candidate.UserId == challenge.UserId &&
                    candidate.Purpose == challenge.Purpose &&
                    candidate.Username == challenge.Username &&
                    candidate.OptionsJson == challenge.OptionsJson,
                    cancellationToken);
            if (!alreadyCommitted) throw;
        }
    }

    private async Task<WebAuthnChallenge?> ClaimChallengeAsync(
        string challengeId,
        string purpose,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var query = _context.WebAuthnChallenges
            .Where(challenge => challenge.Id == challengeId && challenge.Purpose == purpose);
        if (userId != null)
        {
            query = query.Where(challenge => challenge.UserId == userId);
        }

        var challenge = await query.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (challenge == null || challenge.ExpiresAt < now) return null;

        if (_context.Database.IsRelational())
        {
            var claimed = await query
                .Where(candidate => candidate.ExpiresAt >= now)
                .ExecuteDeleteAsync(cancellationToken);
            return claimed == 1 ? challenge : null;
        }

        _context.WebAuthnChallenges.Remove(challenge);
        await _context.SaveChangesAsync(cancellationToken);
        return challenge;
    }

    private async Task CleanupExpiredChallengesAsync(CancellationToken cancellationToken)
    {
        var userId = _context.CurrentUserId;
        var expired = await _context.WebAuthnChallenges
            .Where(c => c.ExpiresAt < DateTime.UtcNow && (userId == null || c.UserId == userId))
            .ToListAsync(cancellationToken);
        if (expired.Count > 0)
        {
            _context.WebAuthnChallenges.RemoveRange(expired);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                foreach (var entry in exception.Entries.Where(e => e.State == EntityState.Deleted))
                    entry.State = EntityState.Detached;
            }
        }
    }
}
