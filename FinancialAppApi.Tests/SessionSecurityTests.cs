using FinancialAppApi.Controllers;
using FinancialAppApi.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Tests;

public class SessionSecurityTests
{
    private static AppUser SeedUser(FinancialAppApi.Database.AppDbContext context, string username, string password)
    {
        var hasher = new PasswordHasher<string>();
        var user = new AppUser { Id = TestHelpers.DefaultUserId, Username = username };
        user.PasswordHash = hasher.HashPassword(user.Username, password);
        context.AppUsers.Add(user);
        context.SaveChanges();
        return user;
    }

    [Fact]
    public async Task SessionCap_EvictsLeastRecentlyActiveSession_NotOldestCreated()
    {
        using var context = TestHelpers.NewInMemoryContext();
        SeedUser(context, "alice", "password123");

        // Session "old-but-active" is the oldest by creation time, but was used recently.
        // Session "new-but-idle" was created after it, but has gone idle for longer.
        // A pure created-at FIFO would evict "old-but-active"; the fix should evict
        // "new-but-idle" instead since it's the least recently active of the five.
        var now = DateTime.UtcNow;
        context.UserSessions.AddRange(
            new UserSession { Token = "t1", Username = "alice", CreatedAt = now.AddDays(-5), ExpiresAt = now.AddDays(2), DeviceId = "old-but-active", LastActiveAt = now.AddMinutes(-1) },
            new UserSession { Token = "t2", Username = "alice", CreatedAt = now.AddDays(-4), ExpiresAt = now.AddDays(2), DeviceId = "d2", LastActiveAt = now.AddHours(-2) },
            new UserSession { Token = "t3", Username = "alice", CreatedAt = now.AddDays(-3), ExpiresAt = now.AddDays(2), DeviceId = "d3", LastActiveAt = now.AddHours(-3) },
            new UserSession { Token = "t4", Username = "alice", CreatedAt = now.AddDays(-2), ExpiresAt = now.AddDays(2), DeviceId = "new-but-idle", LastActiveAt = now.AddDays(-1) },
            new UserSession { Token = "t5", Username = "alice", CreatedAt = now.AddDays(-1), ExpiresAt = now.AddDays(2), DeviceId = "d5", LastActiveAt = now.AddHours(-4) }
        );
        context.SaveChanges();

        var controller = TestHelpers.NewAuthController(context);
        var result = await controller.Login(new LoginRequest { Username = "alice", Password = "password123", DeviceId = "d6-new-login" });
        Assert.IsType<OkObjectResult>(result);

        var remainingDeviceIds = context.UserSessions.Where(s => s.Username == "alice").Select(s => s.DeviceId).ToList();
        Assert.Contains("old-but-active", remainingDeviceIds);
        Assert.DoesNotContain("new-but-idle", remainingDeviceIds);
        Assert.Equal(5, remainingDeviceIds.Count);
    }

    [Fact]
    public async Task GetSessions_NeverExposesRawToken()
    {
        using var context = TestHelpers.NewInMemoryContext();
        SeedUser(context, "alice", "password123");
        context.UserSessions.Add(new UserSession
        {
            Token = "super-secret-live-token",
            Username = "alice",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        context.SaveChanges();

        var controller = TestHelpers.NewAuthController(context);
        TestHelpers.SetUsername(controller, "alice");

        var result = await controller.GetSessions();
        var ok = Assert.IsType<OkObjectResult>(result);

        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.DoesNotContain("super-secret-live-token", json);
        Assert.DoesNotContain("\"Token\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Id\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RevokeSession_ByIdOnly_CannotBePerformedWithRawToken()
    {
        using var context = TestHelpers.NewInMemoryContext();
        SeedUser(context, "alice", "password123");
        var session = new UserSession
        {
            Token = "some-token",
            Username = "alice",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        context.UserSessions.Add(session);
        context.SaveChanges();

        var controller = TestHelpers.NewAuthController(context);
        TestHelpers.SetUsername(controller, "alice");

        // The route parameter is a Guid (the session Id), so a raw token string is never a
        // valid revoke key -- this documents that behavior at the controller-logic level.
        await controller.RevokeSession(session.Id);

        Assert.Empty(context.UserSessions.Where(s => s.Username == "alice"));
    }
}
