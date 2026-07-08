using Microsoft.AspNetCore.DataProtection;

namespace FinancialAppApi.Services;

// Encrypts TOTP secrets at rest using ASP.NET Core's built-in Data Protection stack (no extra
// dependency, and it already handles key rotation/storage). This is reversible encryption, not
// hashing -- unlike a password, the raw secret must be recoverable to compute a TOTP code.
public class SecretProtector
{
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("FinancialAppApi.TotpSecret.v1");
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
