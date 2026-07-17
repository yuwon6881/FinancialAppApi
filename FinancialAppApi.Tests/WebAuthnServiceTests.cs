using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Mvc;

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

    private static WebAuthnService NewService(Database.AppDbContext context)
    {
        return new WebAuthnService(
            context,
            TestHelpers.NewConfiguration(),
            new AuthSessionService(context));
    }

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
