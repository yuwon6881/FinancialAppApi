using System.Security.Cryptography;
using OtpNet;

namespace FinancialAppApi.Services;

// Thin wrapper around Otp.NET so the controller only ever deals in Base32 secrets and
// six-digit codes, not the underlying RFC 6238 mechanics.
public class TotpService
{
    private const string Issuer = "FinancialApp";
    private const int SecretLengthBytes = 20; // 160 bits, matches most authenticator apps' expectations

    public string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretLengthBytes);
        return Base32Encoding.ToString(bytes);
    }

    public string BuildOtpAuthUri(string secret, string username)
    {
        var label = Uri.EscapeDataString($"{Issuer}:{username}");
        var issuer = Uri.EscapeDataString(Issuer);
        return $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&digits=6&period=30";
    }

    // Accepts the current time step and one step on either side to absorb clock drift
    // between the server and the user's device.
    public bool ValidateCode(string secret, string code)
        => ValidateCode(secret, code, out _);

    public bool ValidateCode(string secret, string code, out long timeStepMatched)
    {
        timeStepMatched = 0;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var totp = new Totp(Base32Encoding.ToBytes(secret));
        return totp.VerifyTotp(code.Trim(), out timeStepMatched, new VerificationWindow(previous: 1, future: 1));
    }
}
