using System.Net.Http.Headers;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.AspNetCore.Identity;

namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// Base for HTTP-level integration tests. xUnit creates one instance per test method, so each
/// test gets its own <see cref="FinancialApiFactory"/> (and thus its own isolated InMemory DB).
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    protected readonly FinancialApiFactory Factory = new();

    /// <summary>An unauthenticated client hitting the real pipeline.</summary>
    protected HttpClient CreateClient() => Factory.CreateClient();

    /// <summary>Hashes a password exactly as AuthAccountService does (hasher keyed on username).</summary>
    protected static string HashPassword(string username, string password) =>
        new PasswordHasher<string>().HashPassword(username, password);

    // The 10 default transaction categories are seeded via a migration InsertData, which the
    // EF InMemory provider does not run — so tests seed them explicitly to match production.
    private static readonly (string Id, string Name)[] DefaultCategories =
    [
        ("cat-1", "Salary"), ("cat-2", "Social"), ("cat-3", "Food"), ("cat-4", "Hobbies"),
        ("cat-5", "Software"), ("cat-6", "Investment"), ("cat-7", "Entertainment"),
        ("cat-8", "Transport"), ("cat-9", "Other"), ("cat-10", "Transfer"),
        ("cat-11", "Interest"),
    ];

    /// <summary>The four ledger buckets, each of which now needs at least one explicit account.</summary>
    protected static readonly string[] LedgerBuckets = ["Essentials", "Growth", "Stability", "Rewards"];

    // Account placement is explicit everywhere: there is no default account per bucket and no
    // fallback, so a transaction that names no account in its bucket is rejected. Provisioning no
    // longer creates accounts (the user does, behind the coverage gate), so tests seed their own
    // under a deterministic per-user id and send it on the wire.
    protected static string AccountIdFor(string bucket, string username = "alice") =>
        $"acct-{username}-{bucket.ToLowerInvariant()}";

    /// <summary>Seeds a user and a valid, non-expired session; returns the bearer token.</summary>
    protected async Task<string> SeedUserAndSessionAsync(
        string username = "alice",
        string password = "Password123!",
        bool seedCategories = true,
        bool seedAccounts = true)
    {
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await Factory.WithDbContextAsync(async db =>
        {
            var userId = Guid.NewGuid().ToString();
            db.SetCurrentUser(userId);
            db.AppUsers.Add(new AppUser
            {
                Id = userId,
                Username = username,
                PasswordHash = HashPassword(username, password),
            });

            if (seedCategories)
            {
                DbSeeder.EnsureUserDefaults(db, userId);
                foreach (var category in db.ChangeTracker.Entries<TransactionCategory>()
                             .Where(entry => entry.State == Microsoft.EntityFrameworkCore.EntityState.Added))
                {
                    var match = DefaultCategories.First(item => item.Name == category.Entity.Name);
                    category.Entity.Id = match.Id;
                }
            }

            if (seedAccounts)
            {
                foreach (var bucket in LedgerBuckets)
                {
                    db.LedgerAccounts.Add(new LedgerAccount
                    {
                        Id = AccountIdFor(bucket, username),
                        UserId = userId,
                        Name = $"{bucket} balance",
                        Bucket = bucket,
                        Kind = LedgerAccountKind.Other,
                        IsArchived = false,
                    });
                }
            }

            db.UserSessions.Add(new UserSession
            {
                Token = token,
                UserId = userId,
                Username = username,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
                IsLocked = false,
            });
            await db.SaveChangesAsync();
        });
        return token;
    }

    /// <summary>A client that sends <c>Authorization: Bearer &lt;token&gt;</c> on every request.</summary>
    protected HttpClient CreateAuthenticatedClient(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Seeds a user+session and returns a ready-to-use authenticated client.</summary>
    protected async Task<HttpClient> CreateSignedInClientAsync(string username = "alice")
    {
        var token = await SeedUserAndSessionAsync(username);
        return CreateAuthenticatedClient(token);
    }

    public void Dispose() => Factory.Dispose();
}
