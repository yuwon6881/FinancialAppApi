using FinancialAppApi.Services;
using OtpNet;
using Xunit;

namespace FinancialAppApi.Tests;

public class TotpServiceTests
{
    [Fact]
    public void ValidateCode_AcceptsCurrentCode()
    {
        var service = new TotpService();
        var secret = service.GenerateSecret();

        var totp = new Totp(Base32Encoding.ToBytes(secret));
        var code = totp.ComputeTotp();

        Assert.True(service.ValidateCode(secret, code));
    }

    [Fact]
    public void ValidateCode_RejectsGarbageCode()
    {
        var service = new TotpService();
        var secret = service.GenerateSecret();

        Assert.False(service.ValidateCode(secret, "000000"));
        Assert.False(service.ValidateCode(secret, "not-a-code"));
    }

    [Fact]
    public void BuildOtpAuthUri_ContainsSecretAndIssuer()
    {
        var service = new TotpService();
        var secret = service.GenerateSecret();

        var uri = service.BuildOtpAuthUri(secret, "alice");

        Assert.Contains(secret, uri);
        Assert.Contains("FinancialApp", uri);
        Assert.StartsWith("otpauth://totp/", uri);
    }
}
