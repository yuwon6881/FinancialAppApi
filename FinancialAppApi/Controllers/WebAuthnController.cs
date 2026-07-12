using Fido2NetLib;
using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Filters;
using FinancialAppApi.Extensions;
using FinancialAppApi.Services;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/auth/webauthn")]
public class WebAuthnController : ControllerBase
{
    private readonly WebAuthnService _webAuthnService;
    private readonly AuthCookieService _authCookieService;

    public WebAuthnController(WebAuthnService webAuthnService, AuthCookieService authCookieService)
    {
        _webAuthnService = webAuthnService;
        _authCookieService = authCookieService;
    }

    private string? Username => HttpContext.Items["Username"] as string;
    private string? RequestOrigin => Request.Headers.Origin.ToString();
    private string FallbackOrigin => $"{Request.Scheme}://{Request.Host}";

    // POST api/auth/webauthn/register/options
    [AuthorizeToken]
    [HttpPost("register/options")]
    public async Task<IActionResult> RegisterOptions() =>
        await _webAuthnService.RegisterOptionsAsync(Username, RequestOrigin, FallbackOrigin);

    public class RegisterVerifyRequest
    {
        public string ChallengeId { get; set; } = string.Empty;
        public AuthenticatorAttestationRawResponse Credential { get; set; } = null!;
        public string? DeviceLabel { get; set; }
    }

    // POST api/auth/webauthn/register/verify
    [AuthorizeToken]
    [HttpPost("register/verify")]
    public async Task<IActionResult> RegisterVerify([FromBody] RegisterVerifyRequest request) =>
        await _webAuthnService.RegisterVerifyAsync(
            Username,
            request.ChallengeId,
            request.Credential,
            request.DeviceLabel,
            RequestOrigin,
            FallbackOrigin);

    // POST api/auth/webauthn/login/options
    [HttpPost("login/options")]
    public async Task<IActionResult> LoginOptions() =>
        await _webAuthnService.LoginOptionsAsync(RequestOrigin, FallbackOrigin);

    public class LoginVerifyRequest
    {
        public string ChallengeId { get; set; } = string.Empty;
        public AuthenticatorAssertionRawResponse Credential { get; set; } = null!;
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }
    }

    // POST api/auth/webauthn/login/verify
    [HttpPost("login/verify")]
    public async Task<IActionResult> LoginVerify([FromBody] LoginVerifyRequest request)
    {
        var result = await _webAuthnService.LoginVerifyAsync(
            request.ChallengeId,
            request.Credential,
            request.DeviceId,
            request.DeviceName,
            GetClientIp(),
            Request.Headers["User-Agent"].ToString(),
            RequestOrigin,
            FallbackOrigin);

        if (result is OkObjectResult okResult && okResult.Value is not null)
        {
            var value = okResult.Value;
            var tokenProp = value.GetType().GetProperty("token")?.GetValue(value) as string;
            if (!string.IsNullOrEmpty(tokenProp))
            {
                if (!Request.IsNativeClient()) _authCookieService.IssueSessionCookies(Response, tokenProp);
            }
        }

        return result;
    }

    // POST api/auth/webauthn/assert/options
    [AuthorizeToken]
    [HttpPost("assert/options")]
    public async Task<IActionResult> AssertOptions() =>
        await _webAuthnService.AssertOptionsAsync(Username, RequestOrigin, FallbackOrigin);

    public class AssertVerifyRequest
    {
        public string ChallengeId { get; set; } = string.Empty;
        public AuthenticatorAssertionRawResponse Credential { get; set; } = null!;
    }

    // POST api/auth/webauthn/assert/verify
    [AuthorizeToken]
    [HttpPost("assert/verify")]
    public async Task<IActionResult> AssertVerify([FromBody] AssertVerifyRequest request)
    {
        Request.TryGetBearerToken(out var token);
        return await _webAuthnService.AssertVerifyAsync(
            Username,
            request.ChallengeId,
            request.Credential,
            token,
            RequestOrigin,
            FallbackOrigin);
    }

    // GET api/auth/webauthn/credentials
    [AuthorizeToken]
    [HttpGet("credentials")]
    public async Task<IActionResult> ListCredentials() => await _webAuthnService.ListCredentialsAsync(Username);

    // DELETE api/auth/webauthn/credentials/{id}
    [AuthorizeToken]
    [HttpDelete("credentials/{id}")]
    public async Task<IActionResult> DeleteCredential(string id) =>
        await _webAuthnService.DeleteCredentialAsync(Username, id);

    private string? GetClientIp()
    {
        var forwarded = Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            return forwarded.Split(',')[0].Trim();
        }
        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }
}
