using Google.Apis.Auth;

namespace FinancialAppApi.Services.Push;

// Thin seam around the actual Google-side cryptographic verification (signature, expiry, and
// audience — checked by the underlying library against Google's published JWKS). Isolating it
// behind an interface lets GoogleOidcTokenValidator's own issuer/email business rules be unit
// tested with a fake payload instead of requiring a real Google-signed token and network access.
public interface IGoogleIdTokenVerifier
{
    Task<GoogleJsonWebSignature.Payload> VerifyAsync(string idToken, string audience, CancellationToken cancellationToken = default);
}

public sealed class GoogleIdTokenVerifier : IGoogleIdTokenVerifier
{
    public Task<GoogleJsonWebSignature.Payload> VerifyAsync(string idToken, string audience, CancellationToken cancellationToken = default)
    {
        // ValidateAsync verifies the signature against Google's published JWKS and pins the
        // issuer to accounts.google.com / https://accounts.google.com internally; it also
        // rejects an audience mismatch itself. GoogleOidcTokenValidator re-checks issuer and
        // audience explicitly on the returned payload as a documented, testable defense-in-depth.
        return GoogleJsonWebSignature.ValidateAsync(idToken, new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = [audience]
        });
    }
}
