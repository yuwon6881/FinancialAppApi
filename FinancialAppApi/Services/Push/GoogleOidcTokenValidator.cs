using Google.Apis.Auth;

namespace FinancialAppApi.Services.Push;

public sealed class GoogleOidcTokenValidator : IGoogleOidcTokenValidator
{
    // Google's two historically-used issuer strings for ID tokens. The underlying verifier
    // already pins the issuer internally, but this explicit re-check is what makes the "wrong
    // issuer" business rule directly unit-testable via a fake IGoogleIdTokenVerifier payload,
    // and documents the requirement rather than relying solely on library internals.
    private static readonly HashSet<string> AllowedIssuers = new(StringComparer.Ordinal)
    {
        "accounts.google.com",
        "https://accounts.google.com"
    };

    private readonly IConfiguration _configuration;
    private readonly IGoogleIdTokenVerifier _verifier;

    public GoogleOidcTokenValidator(IConfiguration configuration, IGoogleIdTokenVerifier? verifier = null)
    {
        _configuration = configuration;
        _verifier = verifier ?? new GoogleIdTokenVerifier();
    }

    public async Task<GoogleOidcValidationResult> ValidateAsync(string idToken, CancellationToken cancellationToken = default)
    {
        // Fails closed before any network/crypto work: with no configured audience there is no
        // safe value to check the token against, so every request is rejected outright.
        var audience = _configuration["Push:OidcAudience"];
        if (string.IsNullOrWhiteSpace(audience))
        {
            return GoogleOidcValidationResult.Invalid("Push:OidcAudience is not configured.");
        }

        GoogleJsonWebSignature.Payload payload;
        try
        {
            // Verifies the signature against Google's published JWKS and (via the underlying
            // library) the audience; a forged, expired, or wrong-audience token never reaches
            // the explicit checks below.
            payload = await _verifier.VerifyAsync(idToken, audience, cancellationToken);
        }
        catch (InvalidJwtException ex)
        {
            return GoogleOidcValidationResult.Invalid(ex.Message);
        }

        if (!AllowedIssuers.Contains(payload.Issuer ?? string.Empty))
        {
            return GoogleOidcValidationResult.Invalid("Unexpected token issuer.");
        }

        var audiences = payload.AudienceAsList ?? [];
        if (!audiences.Contains(audience))
        {
            return GoogleOidcValidationResult.Invalid("Unexpected token audience.");
        }

        if (!payload.EmailVerified || string.IsNullOrWhiteSpace(payload.Email))
        {
            return GoogleOidcValidationResult.Invalid("Service account email is not verified.");
        }

        return GoogleOidcValidationResult.Valid(payload.Email);
    }
}
