using FinancialAppApi.Models;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public class AuthSessionServiceTests
{
    [Fact]
    public async Task CreateSessionAsync_EvictsLeastRecentlyActiveSession()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var user = new AppUser { Id = TestHelpers.DefaultUserId, Username = "alice" };
        context.AppUsers.Add(user);
        var now = DateTime.UtcNow;
        context.UserSessions.AddRange(
            new UserSession { Token = "t1", Username = "alice", CreatedAt = now.AddDays(-5), ExpiresAt = now.AddDays(2), DeviceId = "old-but-active", LastActiveAt = now.AddMinutes(-1) },
            new UserSession { Token = "t2", Username = "alice", CreatedAt = now.AddDays(-4), ExpiresAt = now.AddDays(2), DeviceId = "d2", LastActiveAt = now.AddHours(-2) },
            new UserSession { Token = "t3", Username = "alice", CreatedAt = now.AddDays(-3), ExpiresAt = now.AddDays(2), DeviceId = "d3", LastActiveAt = now.AddHours(-3) },
            new UserSession { Token = "t4", Username = "alice", CreatedAt = now.AddDays(-2), ExpiresAt = now.AddDays(2), DeviceId = "new-but-idle", LastActiveAt = now.AddDays(-1) },
            new UserSession { Token = "t5", Username = "alice", CreatedAt = now.AddDays(-1), ExpiresAt = now.AddDays(2), DeviceId = "d5", LastActiveAt = now.AddHours(-4) });
        await context.SaveChangesAsync();
        var service = new AuthSessionService(context);

        await service.CreateSessionAsync(user, "d6-new-login", null, "127.0.0.1", "test");

        var remainingDeviceIds = context.UserSessions.Where(s => s.Username == "alice").Select(s => s.DeviceId).ToList();
        Assert.Contains("old-but-active", remainingDeviceIds);
        Assert.DoesNotContain("new-but-idle", remainingDeviceIds);
        Assert.Equal(5, remainingDeviceIds.Count);
    }

    [Fact]
    public async Task GetSessionsAsync_DoesNotExposeRawToken()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.UserSessions.Add(new UserSession
        {
            Token = "super-secret-live-token",
            Username = "alice",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await context.SaveChangesAsync();
        var service = new AuthSessionService(context);

        var sessions = await service.GetSessionsAsync("alice", "super-secret-live-token");

        var json = System.Text.Json.JsonSerializer.Serialize(sessions);
        Assert.DoesNotContain("super-secret-live-token", json);
        Assert.DoesNotContain("\"Token\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(sessions.Single().IsCurrent);
    }

    [Fact]
    public async Task LockAndUnlockSessionAsync_UpdateLockState()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.UserSessions.Add(new UserSession
        {
            Token = "token",
            Username = "alice",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await context.SaveChangesAsync();
        var service = new AuthSessionService(context);

        Assert.True(await service.LockSessionAsync("token"));
        Assert.True(context.UserSessions.Single().IsLocked);

        await service.UnlockSessionAsync("token");
        Assert.False(context.UserSessions.Single().IsLocked);
    }

    [Fact]
    public async Task RevokeOtherSessionsAsync_WithMissingCurrentToken_IsANoOp()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.UserSessions.Add(new UserSession
        {
            Token = "only-session",
            Username = "alice",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await context.SaveChangesAsync();
        var service = new AuthSessionService(context);

        var revoked = await service.RevokeOtherSessionsAsync("alice", null);

        Assert.Equal(0, revoked);
        Assert.Single(context.UserSessions);
    }
}
