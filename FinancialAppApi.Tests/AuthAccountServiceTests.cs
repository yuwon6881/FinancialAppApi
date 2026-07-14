using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using OtpNet;
using System.Text.Json;

namespace FinancialAppApi.Tests;

public class AuthAccountServiceTests
{
    [Fact]
    public async Task RegisterAsync_CreatesFirstUser()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);

        var result = await service.RegisterAsync("alice", "password123");

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(context.AppUsers);
        Assert.Equal("alice", context.AppUsers.Single().Username);
    }

    [Fact]
    public async Task LoginAsync_LocksAccountAfterRepeatedFailures()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedUser(context, "alice", "correct-password");
        var service = NewService(context, TestHelpers.NewConfiguration(("Auth:MaxFailedLoginAttempts", "2"), ("Auth:LockoutMinutes", "15")));

        await service.LoginAsync("alice", "wrong", null, null, null, null);
        var secondFailure = await service.LoginAsync("alice", "wrong", null, null, null, null);
        var lockedResult = await service.LoginAsync("alice", "correct-password", null, null, null, null);

        Assert.IsType<UnauthorizedObjectResult>(secondFailure);
        var statusResult = Assert.IsType<ObjectResult>(lockedResult);
        Assert.Equal(429, statusResult.StatusCode);
    }

    [Fact]
    public async Task VerifyPasswordAsync_UnlocksCurrentSession()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedUser(context, "alice", "password123");
        context.UserSessions.Add(new UserSession
        {
            Token = "token",
            Username = "alice",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsLocked = true
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.VerifyPasswordAsync("alice", "password123", "token");

        Assert.IsType<OkObjectResult>(result);
        Assert.False(context.UserSessions.Single().IsLocked);
    }

    [Fact]
    public async Task LoginTwoFactorAsync_RejectsAReplayedTotpCode()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var provider = new EphemeralDataProtectionProvider();
        var protector = new SecretProtector(provider);
        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        SeedUser(context, "alice", "password123", protector.Protect(secret));
        var service = NewService(context, provider: provider);

        var firstPending = PendingToken(await service.LoginAsync("alice", "password123", null, null, null, null));
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();
        Assert.IsType<OkObjectResult>(await service.LoginTwoFactorAsync(firstPending, code, null, null));

        var secondPending = PendingToken(await service.LoginAsync("alice", "password123", null, null, null, null));
        var replay = await service.LoginTwoFactorAsync(secondPending, code, null, null);

        Assert.IsType<UnauthorizedObjectResult>(replay);
    }

    [Fact]
    public async Task LoginTwoFactorAsync_LockoutSpansReplacementPendingTokens()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var provider = new EphemeralDataProtectionProvider();
        var protector = new SecretProtector(provider);
        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        SeedUser(context, "alice", "password123", protector.Protect(secret));
        var config = TestHelpers.NewConfiguration(
            ("Auth:MaxTwoFactorAttempts", "2"),
            ("Auth:TwoFactorLockoutMinutes", "15"));
        var service = NewService(context, config, provider);

        var firstPending = PendingToken(await service.LoginAsync("alice", "password123", null, null, null, null));
        Assert.IsType<UnauthorizedObjectResult>(await service.LoginTwoFactorAsync(firstPending, "000000", null, null));

        var replacementPending = PendingToken(await service.LoginAsync("alice", "password123", null, null, null, null));
        var locked = await service.LoginTwoFactorAsync(replacementPending, "000000", null, null);

        Assert.Equal(429, Assert.IsType<ObjectResult>(locked).StatusCode);
        Assert.Empty(context.PendingTwoFactors);
    }

    [Fact]
    public async Task LoginAsync_SweepsAbandonedExpiredTwoFactorTokens()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var provider = new EphemeralDataProtectionProvider();
        var protector = new SecretProtector(provider);
        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        SeedUser(context, "alice", "password123", protector.Protect(secret));
        context.PendingTwoFactors.Add(new PendingTwoFactor
        {
            Username = "alice",
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await context.SaveChangesAsync();
        var service = NewService(context, provider: provider);

        await service.LoginAsync("alice", "password123", null, null, null, null);

        Assert.Single(context.PendingTwoFactors);
        Assert.All(context.PendingTwoFactors, pending => Assert.True(pending.ExpiresAt > DateTime.UtcNow));
    }

    private static AuthAccountService NewService(
        Database.AppDbContext context,
        IConfiguration? configuration = null,
        IDataProtectionProvider? provider = null)
    {
        var protector = new SecretProtector(provider ?? new EphemeralDataProtectionProvider());
        var sessionService = new AuthSessionService(context);
        return new AuthAccountService(
            context,
            configuration ?? TestHelpers.NewConfiguration(),
            new TotpService(),
            protector,
            new RecoveryCodeService(context),
            sessionService);
    }

    private static void SeedUser(
        Database.AppDbContext context,
        string username,
        string password,
        string? protectedTotpSecret = null)
    {
        var hasher = new PasswordHasher<string>();
        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Username = username,
            TotpEnabled = protectedTotpSecret != null,
            TotpSecret = protectedTotpSecret
        };
        user.PasswordHash = hasher.HashPassword(user.Username, password);
        context.AppUsers.Add(user);
        context.SaveChanges();
    }

    private static string PendingToken(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return json.RootElement.GetProperty("pendingToken").GetString()!;
    }
}
