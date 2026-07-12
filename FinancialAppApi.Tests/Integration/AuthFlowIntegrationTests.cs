using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// End-to-end auth workflows through the real HTTP pipeline: registration gating, login,
/// TOTP two-factor, recovery codes, login lockout, and session revocation.
/// </summary>
public class AuthFlowIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task Status_OnEmptyDatabase_ReportsNotRegistered()
    {
        var client = CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");

        Assert.False(body.GetProperty("isRegistered").GetBoolean());
        Assert.False(body.GetProperty("hasFingerprint").GetBoolean());
    }

    [Fact]
    public async Task Register_ThenLogin_IssuesUsableToken()
    {
        var client = CreateClient();

        var register = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "alice", password = "Password123!" });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "alice", password = "Password123!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
        var token = loginBody.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal("alice", loginBody.GetProperty("username").GetString());

        // The freshly-issued token authorizes a protected route.
        var authed = CreateAuthenticatedClient(token!);
        var protectedResponse = await authed.GetAsync("/api/transactions");
        Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
    }

    [Fact]
    public async Task Register_WhenUserAlreadyExists_IsClosed()
    {
        var client = CreateClient();
        await client.PostAsJsonAsync("/api/auth/register",
            new { username = "alice", password = "Password123!" });

        var second = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "bob", password = "Password123!" });

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        await SeedUserAndSessionAsync();
        var client = CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "alice", password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Login_AfterMaxFailedAttempts_LocksOutWith429()
    {
        await SeedUserAndSessionAsync();
        var client = CreateClient();

        // Default Auth:MaxFailedLoginAttempts is 5. The 5th failure triggers lockout but still
        // returns 401; the 6th (while locked) returns 429.
        for (var i = 0; i < 5; i++)
        {
            var attempt = await client.PostAsJsonAsync("/api/auth/login",
                new { username = "alice", password = "wrong-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        var locked = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "alice", password = "Password123!" });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task Login_WithTotpEnabled_RequiresSecondFactor_AndCompletesWithCode()
    {
        // Seed a user with TOTP enabled and a known secret (stored encrypted via SecretProtector).
        var secretKey = KeyGeneration.GenerateRandomKey(20);
        var base32Secret = Base32Encoding.ToString(secretKey);

        await Factory.WithDbContextAsync(async db =>
        {
            using var scope = Factory.Services.CreateScope();
            var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
            db.AppUsers.Add(new AppUser
            {
                Id = Guid.NewGuid().ToString(),
                Username = "alice",
                PasswordHash = HashPassword("alice", "Password123!"),
                TotpEnabled = true,
                TotpSecret = protector.Protect(base32Secret),
            });
            await db.SaveChangesAsync();
        });

        var client = CreateClient();

        // Step 1: password login signals that a second factor is required, no token yet.
        var first = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "alice", password = "Password123!" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(firstBody.GetProperty("requiresTwoFactor").GetBoolean());
        var pendingToken = firstBody.GetProperty("pendingToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(pendingToken));

        // Step 2: submit a valid TOTP code to complete login.
        var code = new Totp(secretKey).ComputeTotp();
        var second = await client.PostAsJsonAsync("/api/auth/login/2fa",
            new { pendingToken, code });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(secondBody.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task TwoFactorLogin_WithInvalidCode_Returns401()
    {
        var secretKey = KeyGeneration.GenerateRandomKey(20);
        var base32Secret = Base32Encoding.ToString(secretKey);
        await Factory.WithDbContextAsync(async db =>
        {
            using var scope = Factory.Services.CreateScope();
            var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
            db.AppUsers.Add(new AppUser
            {
                Id = Guid.NewGuid().ToString(),
                Username = "alice",
                PasswordHash = HashPassword("alice", "Password123!"),
                TotpEnabled = true,
                TotpSecret = protector.Protect(base32Secret),
            });
            await db.SaveChangesAsync();
        });
        var client = CreateClient();
        var first = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "alice", password = "Password123!" });
        var pendingToken = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("pendingToken").GetString();

        var second = await client.PostAsJsonAsync("/api/auth/login/2fa",
            new { pendingToken, code = "000000" });

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Sessions_ListAndRevoke_RoundTrips()
    {
        var token = await SeedUserAndSessionAsync();
        var client = CreateAuthenticatedClient(token);

        // Add a second session for the same user so there is something to revoke.
        Guid otherId = default;
        await Factory.WithDbContextAsync(async db =>
        {
            var other = new UserSession
            {
                Token = Guid.NewGuid().ToString("N"),
                Username = "alice",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            };
            db.UserSessions.Add(other);
            await db.SaveChangesAsync();
            otherId = other.Id;
        });

        var list = await client.GetFromJsonAsync<JsonElement>("/api/auth/sessions");
        Assert.True(list.GetArrayLength() >= 2);

        var revoke = await client.DeleteAsync($"/api/auth/sessions/{otherId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        await Factory.WithDbContextAsync(async db =>
        {
            var stillThere = await db.UserSessions.AnyAsync(s => s.Id == otherId);
            Assert.False(stillThere);
        });
    }
}
