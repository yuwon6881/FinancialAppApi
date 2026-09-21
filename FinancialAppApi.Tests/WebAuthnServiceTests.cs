using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class WebAuthnServiceTests
{
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

    [Fact]
    public async Task RestoreOptionsAsync_RefusesWhenTheAccountOwnsNoCredential()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);

        var result = await service.RestoreOptionsAsync("alice", "https://example.com", "https://example.com");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // Unlocking a session and re-learning which credential lives on this browser are different
    // outcomes, so their challenges must not be interchangeable: a restore challenge redeemed at
    // the assert endpoint would lift a lock the user set without ever being issued for that.
    [Fact]
    public async Task RestoreChallengeCannotBeRedeemedAsASessionUnlock()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = NewSqliteContext(connection);
        await context.Database.EnsureCreatedAsync();
        context.AppUsers.Add(NewUser());
        context.WebAuthnCredentials.Add(NewCredential("alice", [1, 2, 3], "Phone"));
        await context.SaveChangesAsync();
        var service = NewService(context);
        var challengeId = await IssuedChallengeId(
            await service.RestoreOptionsAsync("alice", "https://example.com", "https://example.com"));

        var result = await service.AssertVerifyAsync(
            "alice",
            challengeId,
            NewAssertionResponse([1, 2, 3]),
            currentToken: "token",
            "https://example.com",
            "https://example.com");

        Assert.IsType<UnauthorizedObjectResult>(result);
        // Still unspent, so the honest restore it was issued for can go ahead.
        Assert.Single(context.WebAuthnChallenges);
    }

    // A passkey reached over hybrid transport belongs to the phone that answered the prompt, not
    // to this browser. Recording it would arm the installed-PWA launch gate against a credential
    // the device cannot produce on its own.
    [Fact]
    public async Task RestoreVerifyAsync_RejectsACredentialHeldOnAnotherDevice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = NewSqliteContext(connection);
        await context.Database.EnsureCreatedAsync();
        context.AppUsers.Add(NewUser());
        context.WebAuthnCredentials.Add(NewCredential("alice", [1, 2, 3], "Phone"));
        await context.SaveChangesAsync();
        var service = NewService(context);
        var challengeId = await IssuedChallengeId(
            await service.RestoreOptionsAsync("alice", "https://example.com", "https://example.com"));

        var result = await service.RestoreVerifyAsync(
            "alice",
            challengeId,
            NewAssertionResponse([1, 2, 3]),
            authenticatorAttachment: "cross-platform",
            "https://example.com",
            "https://example.com");

        Assert.IsType<BadRequestObjectResult>(result);
        // The rejected attempt still burns the challenge, so retrying costs a fresh ceremony.
        Assert.Empty(context.WebAuthnChallenges);
    }

    [Fact]
    public async Task RestoreVerifyAsync_RejectsACredentialThisAccountDoesNotOwn()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = NewSqliteContext(connection);
        await context.Database.EnsureCreatedAsync();
        context.AppUsers.Add(NewUser());
        context.WebAuthnCredentials.Add(NewCredential("alice", [1, 2, 3], "Phone"));
        await context.SaveChangesAsync();
        var service = NewService(context);
        var challengeId = await IssuedChallengeId(
            await service.RestoreOptionsAsync("alice", "https://example.com", "https://example.com"));

        var result = await service.RestoreVerifyAsync(
            "alice",
            challengeId,
            NewAssertionResponse([9, 9, 9]),
            authenticatorAttachment: "platform",
            "https://example.com",
            "https://example.com");

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    private static Task<string> IssuedChallengeId(IActionResult optionsResult)
    {
        var ok = Assert.IsType<OkObjectResult>(optionsResult);
        var value = ok.Value!;
        var challengeId = value.GetType().GetProperty("challengeId")!.GetValue(value) as string;
        Assert.False(string.IsNullOrEmpty(challengeId));
        return Task.FromResult(challengeId!);
    }

    private static Fido2NetLib.AuthenticatorAssertionRawResponse NewAssertionResponse(byte[] credentialId) => new()
    {
        Id = Convert.ToBase64String(credentialId),
        RawId = credentialId,
        Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
        Response = new Fido2NetLib.AuthenticatorAssertionRawResponse.AssertionResponse
        {
            AuthenticatorData = [],
            Signature = [],
            ClientDataJson = [],
            UserHandle = System.Text.Encoding.UTF8.GetBytes("alice"),
        },
    };

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
