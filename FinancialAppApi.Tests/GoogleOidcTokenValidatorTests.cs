using FinancialAppApi.Services.Push;
using Google.Apis.Auth;

namespace FinancialAppApi.Tests;

public class GoogleOidcTokenValidatorTests
{
    [Fact]
    public async Task ValidateAsync_FailsClosed_WhenAudienceIsNotConfigured()
    {
        var configuration = TestHelpers.NewConfiguration(("Push:OidcAudience", ""));
        var verifier = new FakeVerifier(ValidPayload());
        var validator = new GoogleOidcTokenValidator(configuration, verifier);

        var result = await validator.ValidateAsync("token");

        Assert.False(result.IsValid);
        Assert.Null(result.ServiceAccountEmail);
        Assert.Contains("Push:OidcAudience", result.FailureReason);
        // Never even attempts the crypto/network verification without a safe value to check.
        Assert.False(verifier.WasCalled);
    }

    [Fact]
    public async Task ValidateAsync_RejectsAnInvalidOrForgedToken()
    {
        var configuration = TestHelpers.NewConfiguration(("Push:OidcAudience", "https://api.example.com/api/push/dispatch"));
        var verifier = new FakeVerifier(throwException: new InvalidJwtException("Signature verification failed."));
        var validator = new GoogleOidcTokenValidator(configuration, verifier);

        var result = await validator.ValidateAsync("bad-token");

        Assert.False(result.IsValid);
        Assert.Contains("Signature verification failed", result.FailureReason);
    }

    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ValidateAsync_RejectsATokenWithAnUnexpectedIssuer(string? issuer)
    {
        var configuration = TestHelpers.NewConfiguration(("Push:OidcAudience", "aud"));
        var payload = ValidPayload();
        payload.Issuer = issuer;
        var verifier = new FakeVerifier(payload);
        var validator = new GoogleOidcTokenValidator(configuration, verifier);

        var result = await validator.ValidateAsync("token");

        Assert.False(result.IsValid);
        Assert.Contains("issuer", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("accounts.google.com")]
    [InlineData("https://accounts.google.com")]
    public async Task ValidateAsync_AcceptsEitherDocumentedGoogleIssuerString(string issuer)
    {
        var configuration = TestHelpers.NewConfiguration(("Push:OidcAudience", "aud"));
        var payload = ValidPayload();
        payload.Issuer = issuer;
        var verifier = new FakeVerifier(payload);
        var validator = new GoogleOidcTokenValidator(configuration, verifier);

        var result = await validator.ValidateAsync("token");

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_RejectsATokenIssuedForADifferentAudience()
    {
        var configuration = TestHelpers.NewConfiguration(("Push:OidcAudience", "https://api.example.com/api/push/dispatch"));
        var payload = ValidPayload();
        payload.Audience = "https://some-other-service.example.com/endpoint";
        var verifier = new FakeVerifier(payload);
        var validator = new GoogleOidcTokenValidator(configuration, verifier);

        var result = await validator.ValidateAsync("token");

        Assert.False(result.IsValid);
        Assert.Contains("audience", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsATokenWithAnUnverifiedEmail()
    {
        var configuration = TestHelpers.NewConfiguration(("Push:OidcAudience", "aud"));
        var payload = ValidPayload();
        payload.EmailVerified = false;
        var verifier = new FakeVerifier(payload);
        var validator = new GoogleOidcTokenValidator(configuration, verifier);

        var result = await validator.ValidateAsync("token");

        Assert.False(result.IsValid);
        Assert.Contains("verified", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsATokenWithNoEmailClaim()
    {
        var configuration = TestHelpers.NewConfiguration(("Push:OidcAudience", "aud"));
        var payload = ValidPayload();
        payload.Email = null;
        var verifier = new FakeVerifier(payload);
        var validator = new GoogleOidcTokenValidator(configuration, verifier);

        var result = await validator.ValidateAsync("token");

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_AcceptsAValidGoogleSignedServiceAccountToken()
    {
        var configuration = TestHelpers.NewConfiguration(("Push:OidcAudience", "aud"));
        var verifier = new FakeVerifier(ValidPayload());
        var validator = new GoogleOidcTokenValidator(configuration, verifier);

        var result = await validator.ValidateAsync("token");

        Assert.True(result.IsValid);
        Assert.Equal("scheduler@my-project.iam.gserviceaccount.com", result.ServiceAccountEmail);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_PassesTheConfiguredAudienceThroughToTheVerifier()
    {
        var configuration = TestHelpers.NewConfiguration(("Push:OidcAudience", "https://api.example.com/api/push/dispatch"));
        var verifier = new FakeVerifier(ValidPayload());
        var validator = new GoogleOidcTokenValidator(configuration, verifier);

        await validator.ValidateAsync("token");

        Assert.Equal("https://api.example.com/api/push/dispatch", verifier.LastAudience);
    }

    private static GoogleJsonWebSignature.Payload ValidPayload() => new()
    {
        Issuer = "https://accounts.google.com",
        Audience = "aud",
        Email = "scheduler@my-project.iam.gserviceaccount.com",
        EmailVerified = true
    };

    private sealed class FakeVerifier : IGoogleIdTokenVerifier
    {
        private readonly GoogleJsonWebSignature.Payload? _payload;
        private readonly Exception? _throwException;

        public FakeVerifier(GoogleJsonWebSignature.Payload? payload = null, Exception? throwException = null)
        {
            _payload = payload;
            _throwException = throwException;
        }

        public bool WasCalled { get; private set; }
        public string? LastAudience { get; private set; }

        public Task<GoogleJsonWebSignature.Payload> VerifyAsync(string idToken, string audience, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastAudience = audience;
            if (_throwException != null)
            {
                throw _throwException;
            }
            return Task.FromResult(_payload!);
        }
    }
}
