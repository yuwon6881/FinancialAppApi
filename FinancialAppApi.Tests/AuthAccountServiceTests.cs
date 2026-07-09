using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

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

    private static AuthAccountService NewService(
        Database.AppDbContext context,
        IConfiguration? configuration = null)
    {
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        var sessionService = new AuthSessionService(context);
        return new AuthAccountService(
            context,
            configuration ?? TestHelpers.NewConfiguration(),
            new TotpService(),
            protector,
            new RecoveryCodeService(context),
            sessionService);
    }

    private static void SeedUser(Database.AppDbContext context, string username, string password)
    {
        var hasher = new PasswordHasher<string>();
        var user = new AppUser { Id = Guid.NewGuid().ToString(), Username = username };
        user.PasswordHash = hasher.HashPassword(user.Username, password);
        context.AppUsers.Add(user);
        context.SaveChanges();
    }
}
