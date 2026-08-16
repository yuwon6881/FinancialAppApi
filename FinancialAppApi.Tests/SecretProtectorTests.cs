using System.Security.Cryptography;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace FinancialAppApi.Tests;

public class SecretProtectorTests
{
    [Fact]
    public void ProtectAndUnprotect_RoundTripsTheTotpSecret()
    {
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());

        var ciphertext = protector.Protect("JBSWY3DPEHPK3PXP");

        Assert.NotEqual("JBSWY3DPEHPK3PXP", ciphertext);
        Assert.Equal("JBSWY3DPEHPK3PXP", protector.Unprotect(ciphertext));
    }

    [Fact]
    public void Unprotect_RejectsTamperedCiphertext()
    {
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        var ciphertext = protector.Protect("secret");
        var tampered = ciphertext[..^1] + (ciphertext[^1] == 'A' ? 'B' : 'A');

        Assert.Throws<CryptographicException>(() => protector.Unprotect(tampered));
    }

    [Fact]
    public void Unprotect_RemainsCompatibleWhenTheKeyRingRotates()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"financialapp-keyring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using (var services = new ServiceCollection()
                       .AddDataProtection()
                       .PersistKeysToFileSystem(new DirectoryInfo(directory))
                       .SetApplicationName("FinancialAppApi.Tests")
                       .Services
                       .BuildServiceProvider())
            {
                var provider = services.GetRequiredService<IDataProtectionProvider>();
                var keyManager = services.GetRequiredService<IKeyManager>();
                var protector = new SecretProtector(provider);
                var ciphertext = protector.Protect("rotatable-secret");

                keyManager.CreateNewKey(
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddYears(1));

                Assert.Equal("rotatable-secret", protector.Unprotect(ciphertext));
                Assert.Equal("rotatable-secret", new SecretProtector(provider).Unprotect(ciphertext));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
