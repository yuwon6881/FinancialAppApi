using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    public async Task RegisterAsync_WhenClosed_RejectsSecondRegistrationAndProvisionsNothing()
    {
        // Default configuration keeps the app single-user: once anyone is registered, a second
        // registration is refused and no extra account or seeded data is created.
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);
        Assert.IsType<OkObjectResult>(await service.RegisterAsync("alice", "password123"));

        var second = await service.RegisterAsync("bob", "password123");

        Assert.IsType<BadRequestObjectResult>(second);
        Assert.Single(context.AppUsers);
        Assert.Equal("alice", context.AppUsers.Single().Username);
        Assert.Equal(1, await context.FinancialSettings.IgnoreQueryFilters().CountAsync());
        Assert.Equal(10, await context.TransactionCategories.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task RegisterAsync_WhenAdditionalUsersAreEnabled_ProvisionsSeparateUserData()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var configuration = TestHelpers.NewConfiguration(("Auth:AllowAdditionalUsers", "true"));
        var service = NewService(context, configuration);

        Assert.IsType<OkObjectResult>(await service.RegisterAsync("alice", "password123"));
        Assert.IsType<OkObjectResult>(await service.RegisterAsync("bob", "password123"));

        Assert.Equal(2, await context.AppUsers.CountAsync());
        Assert.Equal(2, await context.FinancialSettings.IgnoreQueryFilters().CountAsync());
        Assert.Equal(20, await context.TransactionCategories.IgnoreQueryFilters().CountAsync());
        Assert.All(context.AppUsers, user => Assert.Null(user.RegistrationSlot));
    }

    [Fact]
    public async Task RegisterAsync_WithMaxUsersCap_AllowsUpToLimitThenCloses()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context, TestHelpers.NewConfiguration(("Auth:MaxUsers", "2")));

        Assert.IsType<OkObjectResult>(await service.RegisterAsync("alice", "password123"));
        Assert.IsType<OkObjectResult>(await service.RegisterAsync("bob", "password123"));
        var thirdResult = await service.RegisterAsync("carol", "password123");

        Assert.IsType<BadRequestObjectResult>(thirdResult);
        Assert.Equal(2, await context.AppUsers.CountAsync());
        // Each accepted user occupies a distinct slot in [1..MaxUsers]; the unique index on that
        // slot is what makes the cap race-safe.
        var slots = await context.AppUsers.Select(u => u.RegistrationSlot).ToListAsync();
        Assert.Equal(new int?[] { 1, 2 }, slots.OrderBy(slot => slot));
    }

    [Fact]
    public async Task GetStatusAsync_ReportsRegistrationOpenUntilCapReached()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context, TestHelpers.NewConfiguration(("Auth:MaxUsers", "2")));

        Assert.True(ResultBody(await service.GetStatusAsync()).GetProperty("registrationOpen").GetBoolean());

        await service.RegisterAsync("alice", "password123");
        Assert.True(ResultBody(await service.GetStatusAsync()).GetProperty("registrationOpen").GetBoolean());

        await service.RegisterAsync("bob", "password123");
        var full = ResultBody(await service.GetStatusAsync());
        Assert.False(full.GetProperty("registrationOpen").GetBoolean());
        Assert.True(full.GetProperty("isRegistered").GetBoolean());
    }

    [Fact]
    public async Task GetStatusAsync_ScopesDeviceUnlockAvailabilityToRequestedUser()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context, TestHelpers.NewConfiguration(("Auth:MaxUsers", "2")));
        await service.RegisterAsync("alice", "password123");
        await service.RegisterAsync("bob", "password123");
        var alice = await context.AppUsers.SingleAsync(user => user.NormalizedUsername == "ALICE");
        context.SetCurrentUser(alice.Id);
        context.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            CredentialId = [1, 2, 3],
            UserId = alice.Id,
            Username = alice.Username,
            PublicKey = [4, 5, 6],
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var aliceStatus = ResultBody(await service.GetStatusAsync(" Alice "));
        var bobStatus = ResultBody(await service.GetStatusAsync("bob"));

        Assert.True(aliceStatus.GetProperty("hasFingerprint").GetBoolean());
        Assert.False(bobStatus.GetProperty("hasFingerprint").GetBoolean());
    }

    [Fact]
    public async Task RegisterAsync_ReusesASlotFreedByADeletedUser()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context, TestHelpers.NewConfiguration(("Auth:MaxUsers", "2")));
        await service.RegisterAsync("alice", "password123");
        await service.RegisterAsync("bob", "password123");

        var bob = await context.AppUsers.SingleAsync(u => u.NormalizedUsername == "BOB");
        context.AppUsers.Remove(bob);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // With a slot freed, a new registration is accepted again and takes the vacated slot.
        Assert.IsType<OkObjectResult>(await service.RegisterAsync("carol", "password123"));
        var slots = await context.AppUsers.Select(u => u.RegistrationSlot).ToListAsync();
        Assert.Equal(new int?[] { 1, 2 }, slots.OrderBy(slot => slot));
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
        var user = SeedUser(context, "alice", "password123");
        user.PasswordVerificationFailedAttempts = 2;
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

        var body = ResultBody(result);
        Assert.True(body.GetProperty("verified").GetBoolean());
        Assert.False(context.UserSessions.Single().IsLocked);
        Assert.Equal(0, user.PasswordVerificationFailedAttempts);
        Assert.Null(user.PasswordVerificationLockedUntil);
    }

    [Fact]
    public async Task VerifyPasswordAsync_LocksAtThresholdWithoutChangingLoginLockout()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var user = SeedUser(context, "alice", "password123");
        context.UserSessions.Add(new UserSession
        {
            Token = "token",
            Username = "alice",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsLocked = true
        });
        await context.SaveChangesAsync();
        var configuration = TestHelpers.NewConfiguration(
            ("Auth:MaxPasswordVerificationAttempts", "2"),
            ("Auth:PasswordVerificationLockoutMinutes", "15"));
        var service = NewService(context, configuration);

        var firstFailure = ResultBody(await service.VerifyPasswordAsync("alice", "wrong", "token"));
        var thresholdFailure = ResultBody(await service.VerifyPasswordAsync("alice", "wrong", "token"));
        var correctPasswordWhileLocked = ResultBody(
            await service.VerifyPasswordAsync("alice", "password123", "token"));

        Assert.False(firstFailure.GetProperty("verified").GetBoolean());
        Assert.False(firstFailure.TryGetProperty("locked", out _));
        Assert.False(thresholdFailure.GetProperty("verified").GetBoolean());
        Assert.True(thresholdFailure.GetProperty("locked").GetBoolean());
        Assert.True(thresholdFailure.GetProperty("retryAfterSeconds").GetInt32() > 0);
        Assert.True(correctPasswordWhileLocked.GetProperty("locked").GetBoolean());
        Assert.True(context.UserSessions.Single().IsLocked);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockedUntil);
        Assert.NotNull(user.PasswordVerificationLockedUntil);
    }

    [Fact]
    public async Task VerifyPasswordAsync_ExpiredVerificationLockoutCanUnlockAndReset()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var user = SeedUser(context, "alice", "password123");
        user.PasswordVerificationFailedAttempts = 3;
        user.PasswordVerificationLockedUntil = DateTime.UtcNow.AddMinutes(-1);
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

        var body = ResultBody(await service.VerifyPasswordAsync("alice", "password123", "token"));

        Assert.True(body.GetProperty("verified").GetBoolean());
        Assert.False(context.UserSessions.Single().IsLocked);
        Assert.Equal(0, user.PasswordVerificationFailedAttempts);
        Assert.Null(user.PasswordVerificationLockedUntil);
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

    private static AppUser SeedUser(
        Database.AppDbContext context,
        string username,
        string password,
        string? protectedTotpSecret = null)
    {
        var hasher = new PasswordHasher<string>();
        var user = new AppUser
        {
            Id = TestHelpers.DefaultUserId,
            Username = username,
            TotpEnabled = protectedTotpSecret != null,
            TotpSecret = protectedTotpSecret
        };
        user.PasswordHash = hasher.HashPassword(user.Username, password);
        context.AppUsers.Add(user);
        context.SaveChanges();
        return user;
    }

    private static JsonElement ResultBody(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return json.RootElement.Clone();
    }

    private static string PendingToken(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return json.RootElement.GetProperty("pendingToken").GetString()!;
    }
}
