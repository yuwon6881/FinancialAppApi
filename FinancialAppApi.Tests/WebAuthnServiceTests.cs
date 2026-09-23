using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class WebAuthnServiceTests
{
    [Fact]
    public void ToAndroidWebAuthnOrigin_UsesTheExactCertificateFingerprint()
    {
        var fingerprint = string.Join(":", Enumerable.Repeat("00", 32));

        Assert.Equal(
            "android:apk-key-hash:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            WebAuthnService.ToAndroidWebAuthnOrigin(fingerprint));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00:11")]
    [InlineData("GG0000000000000000000000000000000000000000000000000000000000000000")]
    public void ToAndroidWebAuthnOrigin_RejectsAnUnconfiguredOrMalformedFingerprint(string? fingerprint)
    {
        Assert.Null(WebAuthnService.ToAndroidWebAuthnOrigin(fingerprint));
    }

    [Fact]
    public async Task LoginOptionsAsync_ReturnsBadRequestWhenNoUserExists()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);

        var result = await service.LoginOptionsAsync("https://example.com", "https://example.com");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("No account registered", System.Text.Json.JsonSerializer.Serialize(badRequest.Value));
    }

    [Fact]
    public async Task ListCredentialsAsync_ReturnsOnlyUsersCredentials()
    {
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.WebAuthnCredentials.AddRange(
            NewCredential("alice", TestHelpers.DefaultUserId, [1, 2, 3], "Phone"),
            NewCredential("bob", "bob-user", [4, 5, 6], "Tablet"));
        await context.SaveChangesAsync();
        context.SetCurrentUser(TestHelpers.DefaultUserId);
        var service = NewService(context);

        var result = await service.ListCredentialsAsync("alice");

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Phone", json);
        Assert.DoesNotContain("Tablet", json);
    }

    [Fact]
    public async Task DeleteCredentialAsync_RemovesMatchingCredential()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.WebAuthnCredentials.Add(NewCredential("alice", [1, 2, 3], "Phone"));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.DeleteCredentialAsync("alice", "010203");

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(context.WebAuthnCredentials);
    }

    [Fact]
    public async Task DeleteCredentialAsync_RejectsInvalidHex()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);

        var result = await service.DeleteCredentialAsync("alice", "not-hex");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PersistChallengeAsync_AcceptsAnIdenticalInsertCommittedBeforeARetry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = NewSqliteContext(connection);
        await context.Database.EnsureCreatedAsync();
        context.AppUsers.Add(NewUser());
        await context.SaveChangesAsync();
        var challenge = NewChallenge();
        context.WebAuthnChallenges.Add(challenge);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var service = NewService(context);

        await service.PersistChallengeAsync(NewChallenge());

        Assert.Single(context.WebAuthnChallenges);
    }

    [Fact]
    public async Task PersistChallengeAsync_DoesNotHideARealPrimaryKeyCollision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = NewSqliteContext(connection);
        await context.Database.EnsureCreatedAsync();
        context.AppUsers.Add(NewUser());
        await context.SaveChangesAsync();
        context.WebAuthnChallenges.Add(NewChallenge());
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var service = NewService(context);
        var collision = NewChallenge();
        collision.OptionsJson = "different-options";

        await Assert.ThrowsAsync<DbUpdateException>(() => service.PersistChallengeAsync(collision));
    }

    private static WebAuthnService NewService(Database.AppDbContext context)
    {
        return new WebAuthnService(
            context,
            TestHelpers.NewConfiguration(),
            new AuthSessionService(context));
    }

    private static Database.AppDbContext NewSqliteContext(SqliteConnection connection)
    {
        var context = new Database.AppDbContext(
            new DbContextOptionsBuilder<Database.AppDbContext>().UseSqlite(connection).Options);
        context.SetCurrentUser(TestHelpers.DefaultUserId);
        return context;
    }

    private static AppUser NewUser() => new()
    {
        Id = TestHelpers.DefaultUserId,
        Username = "alice",
        NormalizedUsername = "ALICE",
        PasswordHash = "test-hash"
    };

    private static WebAuthnChallenge NewChallenge() => new()
    {
        Id = "retry-safe-challenge",
        UserId = TestHelpers.DefaultUserId,
        Purpose = "assert",
        Username = "alice",
        OptionsJson = "same-options",
        ExpiresAt = DateTime.UtcNow.AddMinutes(5)
    };

    private static WebAuthnCredential NewCredential(
        string username,
        byte[] credentialId,
        string label) => NewCredential(username, TestHelpers.DefaultUserId, credentialId, label);

    private static WebAuthnCredential NewCredential(string username, string userId, byte[] credentialId, string label)
    {
        return new WebAuthnCredential
        {
            CredentialId = credentialId,
            UserId = userId,
            Username = username,
            PublicKey = [7, 8, 9],
            SignCount = 0,
            DeviceLabel = label,
            CreatedAt = DateTime.UtcNow
        };
    }
}
