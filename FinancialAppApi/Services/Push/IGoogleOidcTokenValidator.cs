namespace FinancialAppApi.Services.Push;

public sealed record GoogleOidcValidationResult(bool IsValid, string? ServiceAccountEmail, string? FailureReason)
{
    public static GoogleOidcValidationResult Valid(string serviceAccountEmail) => new(true, serviceAccountEmail, null);

    public static GoogleOidcValidationResult Invalid(string reason) => new(false, null, reason);
}

// Verifies a Google-signed OIDC identity token (issuer, signature, audience). Cloud Scheduler
// attaches one of these to its request when invoking the dispatch endpoint; this is what proves
// the caller really is Google infrastructure acting as the configured service account, rather
// than an arbitrary caller replaying a guessed URL.
public interface IGoogleOidcTokenValidator
{
    Task<GoogleOidcValidationResult> ValidateAsync(string idToken, CancellationToken cancellationToken = default);
}
