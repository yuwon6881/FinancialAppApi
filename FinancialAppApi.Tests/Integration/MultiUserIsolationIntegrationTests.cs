using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// End-to-end proof that two authenticated users are fully isolated through the real HTTP
/// pipeline — a user can never read, mutate, or delete another user's records, and each user
/// only ever sees their own defaults. Transaction isolation is covered separately in
/// <see cref="TransactionsIntegrationTests"/>; this focuses on the remaining resources.
/// </summary>
public class MultiUserIsolationIntegrationTests : IntegrationTestBase
{
    private const string AliceUserId = "alice-user";
    private const string BobUserId = "bob-user";

    [Fact]
    public async Task Wishlist_IsPartitionedPerUser()
    {
        var alice = CreateAuthenticatedClient(await SeedUserWithDefaultsAsync("alice", AliceUserId));
        var bob = CreateAuthenticatedClient(await SeedUserWithDefaultsAsync("bob", BobUserId));

        var aliceItemId = await CreateWishlistItemAsync(alice, "Alice Laptop", 1500m);
        var bobItemId = await CreateWishlistItemAsync(bob, "Bob Phone", 900m);

        // Each user's listing contains only their own item.
        var aliceList = await alice.GetFromJsonAsync<JsonElement>("/api/wishlist");
        Assert.Equal(new[] { aliceItemId }, WishlistIds(aliceList));
        var bobList = await bob.GetFromJsonAsync<JsonElement>("/api/wishlist");
        Assert.Equal(new[] { bobItemId }, WishlistIds(bobList));

        // Bob cannot delete Alice's item; it stays intact for Alice.
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/wishlist/{aliceItemId}")).StatusCode);
        var aliceStill = await alice.GetFromJsonAsync<JsonElement>("/api/wishlist");
        Assert.Equal(new[] { aliceItemId }, WishlistIds(aliceStill));
    }

    [Fact]
    public async Task RecurringPayments_ArePartitionedPerUser()
    {
        var alice = CreateAuthenticatedClient(await SeedUserWithDefaultsAsync("alice", AliceUserId));
        var bob = CreateAuthenticatedClient(await SeedUserWithDefaultsAsync("bob", BobUserId));

        await CreateRecurringPaymentAsync(alice, "alice-netflix", "Alice Netflix", -15m, "alice");
        await CreateRecurringPaymentAsync(bob, "bob-spotify", "Bob Spotify", -10m, "bob");

        Assert.Equal(new[] { "alice-netflix" }, await RecurringIds(alice));
        Assert.Equal(new[] { "bob-spotify" }, await RecurringIds(bob));

        // Bob cannot toggle, update, or delete Alice's recurring payment.
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.PutAsJsonAsync("/api/recurring-payments/alice-netflix/toggle", new { active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.DeleteAsync("/api/recurring-payments/alice-netflix")).StatusCode);

        // Alice's payment is untouched.
        Assert.Equal(new[] { "alice-netflix" }, await RecurringIds(alice));
    }

    [Fact]
    public async Task Categories_ArePartitionedPerUser_AndDefaultsAreDisjoint()
    {
        var alice = CreateAuthenticatedClient(await SeedUserWithDefaultsAsync("alice", AliceUserId));
        var bob = CreateAuthenticatedClient(await SeedUserWithDefaultsAsync("bob", BobUserId));

        // Both users start with the same 10 default category names, but as separate rows.
        Assert.Equal(10, (await CategoryNames(alice)).Count);
        Assert.Equal(10, (await CategoryNames(bob)).Count);

        // Alice adds a custom category; it must not appear for Bob.
        var create = await alice.PostAsJsonAsync("/api/categories",
            new { id = "alice-custom-cat", name = "AliceOnlyCategory" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var aliceNames = await CategoryNames(alice);
        Assert.Contains("AliceOnlyCategory", aliceNames);
        Assert.Equal(11, aliceNames.Count);

        var bobNames = await CategoryNames(bob);
        Assert.DoesNotContain("AliceOnlyCategory", bobNames);
        Assert.Equal(10, bobNames.Count);
    }

    [Fact]
    public async Task AiChat_SendsOnlyTheAuthenticatedUsersContextToTheProvider()
    {
        var alice = CreateAuthenticatedClient(await SeedUserWithDefaultsAsync("alice", AliceUserId));
        var bob = CreateAuthenticatedClient(await SeedUserWithDefaultsAsync("bob", BobUserId));

        await CreateTransactionAsync(alice, "alice-ai-transaction", "Alice Private Groceries", "alice");
        await CreateTransactionAsync(bob, "bob-ai-transaction", "Bob Secret Groceries", "bob");
        await CreateRecurringPaymentAsync(alice, "alice-ai-recurring", "Alice Private Streaming", -15m, "alice");
        await CreateRecurringPaymentAsync(bob, "bob-ai-recurring", "Bob Secret Streaming", -10m, "bob");
        await CreateWishlistItemAsync(alice, "Alice Private Laptop", 1500m);
        await CreateWishlistItemAsync(bob, "Bob Secret Phone", 900m);

        var chat = await alice.PostAsJsonAsync("/api/ai/chat", new
        {
            message = "Summarize my recent transactions, recurring payments, and wishlist.",
            conversationState = new { }
        });

        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        var providerPayload = Assert.IsType<string>(Factory.LastAiRequestBody);
        Assert.Contains("Alice Private Groceries", providerPayload);
        Assert.Contains("Alice Private Streaming", providerPayload);
        Assert.Contains("Alice Private Laptop", providerPayload);
        Assert.DoesNotContain("Bob Secret Groceries", providerPayload);
        Assert.DoesNotContain("Bob Secret Streaming", providerPayload);
        Assert.DoesNotContain("Bob Secret Phone", providerPayload);
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>
    /// Seeds a user with their own per-user default categories/settings (ids scoped as
    /// <c>cat-{userId}-n</c> so two users never collide) plus a valid session token.
    /// </summary>
    private async Task<string> SeedUserWithDefaultsAsync(string username, string userId)
    {
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await Factory.WithDbContextAsync(async db =>
        {
            db.SetCurrentUser(userId);
            db.AppUsers.Add(new AppUser
            {
                Id = userId,
                Username = username,
                PasswordHash = HashPassword(username, "Password123!"),
            });
            DbSeeder.EnsureUserDefaults(db, userId);
            // Placement is explicit, so each user needs their own account per bucket before they
            // can record anything; provisioning no longer creates them.
            foreach (var bucket in LedgerBuckets)
            {
                db.LedgerAccounts.Add(new LedgerAccount
                {
                    Id = AccountIdFor(bucket, username),
                    UserId = userId,
                    Name = $"{bucket} balance",
                    Bucket = bucket,
                    Kind = LedgerAccountKind.Other,
                });
            }
            db.UserSessions.Add(new UserSession
            {
                Token = token,
                UserId = userId,
                Username = username,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        });
        return token;
    }

    private static async Task<int> CreateWishlistItemAsync(HttpClient client, string name, decimal price)
    {
        var create = await client.PostAsJsonAsync("/api/wishlist", new
        {
            name,
            price = ObfuscationHelper.Obfuscate(price),
            priority = "Medium",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task CreateTransactionAsync(HttpClient client, string id, string description, string owner)
    {
        var create = await client.PostAsJsonAsync("/api/transactions", new
        {
            id,
            date = "2026-07-15",
            description,
            category = "Food",
            ledgerCategory = "Essentials",
            accountId = AccountIdFor("Essentials", owner),
            amount = ObfuscationHelper.Obfuscate(-25m),
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    private static async Task CreateRecurringPaymentAsync(HttpClient client, string id, string name, decimal amount, string owner)
    {
        var create = await client.PostAsJsonAsync("/api/recurring-payments", new
        {
            id,
            name,
            amount = ObfuscationHelper.Obfuscate(amount),
            frequency = "Monthly",
            category = "Food",
            ledgerCategory = "Essentials",
            accountId = AccountIdFor("Essentials", owner),
            nextDueDate = "2026-08-01",
            dueDate = 1,
            startDate = "2026-01-01",
            active = true,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    private static int[] WishlistIds(JsonElement list) =>
        list.EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).OrderBy(id => id).ToArray();

    private static async Task<string[]> RecurringIds(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/recurring-payments");
        return list.EnumerateArray().Select(item => item.GetProperty("id").GetString()!).OrderBy(id => id).ToArray();
    }

    private static async Task<List<string>> CategoryNames(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/categories");
        return list.EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToList();
    }
}
