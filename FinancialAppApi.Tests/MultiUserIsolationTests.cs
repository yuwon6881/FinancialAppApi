using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FinancialAppApi.Tests;

public class MultiUserIsolationTests
{
    [Fact]
    public async Task FinancialQueriesAndCategoryCache_ArePartitionedByCurrentUser()
    {
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.AppUsers.AddRange(
            new AppUser { Id = "alice-user", Username = "alice", PasswordHash = "hash" },
            new AppUser { Id = "bob-user", Username = "bob", PasswordHash = "hash" });
        context.FinancialSettings.AddRange(
            new FinancialSetting { UserId = "alice-user", Currency = "MYR" },
            new FinancialSetting { UserId = "bob-user", Currency = "EUR" });
        context.TransactionCategories.AddRange(
            new TransactionCategory { Id = "alice-food", UserId = "alice-user", Name = "Alice Food" },
            new TransactionCategory { Id = "bob-food", UserId = "bob-user", Name = "Bob Food" });
        context.Transactions.AddRange(
            NewTransaction("alice-tx", "alice-user", "Alice transaction"),
            NewTransaction("bob-tx", "bob-user", "Bob transaction"));
        context.RecurringPayments.AddRange(
            NewRecurringPayment("alice-recurring", "alice-user", "Alice recurring"),
            NewRecurringPayment("bob-recurring", "bob-user", "Bob recurring"));
        context.WishlistItems.AddRange(
            new WishlistItem { Id = 1, UserId = "alice-user", Name = "Alice wish", Price = 10 },
            new WishlistItem { Id = 2, UserId = "bob-user", Name = "Bob wish", Price = 20 });
        context.VaultDocuments.AddRange(
            new VaultDocument { Id = 1, UserId = "alice-user", StorageObjectPath = "alice/2026/doc1.pdf", OriginalFileName = "alice.pdf", ContentType = "application/pdf", SizeBytes = 100, Sha256 = "a".PadRight(64, '0'), TaxYear = 2026, DocumentType = "Receipt", UploadedAt = DateTime.UtcNow, RetentionUntil = new DateOnly(2033, 12, 31) },
            new VaultDocument { Id = 2, UserId = "bob-user", StorageObjectPath = "bob/2026/doc2.pdf", OriginalFileName = "bob.pdf", ContentType = "application/pdf", SizeBytes = 200, Sha256 = "b".PadRight(64, '0'), TaxYear = 2026, DocumentType = "Receipt", UploadedAt = DateTime.UtcNow, RetentionUntil = new DateOnly(2033, 12, 31) });
        context.CycleBalances.AddRange(
            new CycleBalance { UserId = "alice-user", Year = 2026, MonthIndex = 7, EssentialsBalance = 10 },
            new CycleBalance { UserId = "bob-user", Year = 2026, MonthIndex = 7, EssentialsBalance = 20 });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var categories = new TransactionCategoryService(context, cache);

        context.SetCurrentUser("alice-user");
        Assert.Equal("MYR", (await context.FinancialSettings.SingleAsync()).Currency);
        Assert.Equal("Alice transaction", (await context.Transactions.SingleAsync()).Description);
        Assert.Equal("Alice recurring", (await context.RecurringPayments.SingleAsync()).Name);
        Assert.Equal("Alice wish", (await context.WishlistItems.SingleAsync()).Name);
        Assert.Equal(10, (await context.CycleBalances.SingleAsync()).EssentialsBalance);
        Assert.Equal("Alice Food", Assert.Single(await categories.GetCategoriesAsync()).Name);
        Assert.Equal("alice.pdf", (await context.VaultDocuments.SingleAsync()).OriginalFileName);
        Assert.Null(await context.Transactions.FindAsync("bob-tx"));

        context.ChangeTracker.Clear();
        context.SetCurrentUser("bob-user");
        Assert.Equal("EUR", (await context.FinancialSettings.SingleAsync()).Currency);
        Assert.Equal("Bob transaction", (await context.Transactions.SingleAsync()).Description);
        Assert.Equal("Bob recurring", (await context.RecurringPayments.SingleAsync()).Name);
        Assert.Equal("Bob wish", (await context.WishlistItems.SingleAsync()).Name);
        Assert.Equal(20, (await context.CycleBalances.SingleAsync()).EssentialsBalance);
        Assert.Equal("Bob Food", Assert.Single(await categories.GetCategoriesAsync()).Name);
        Assert.Equal("bob.pdf", (await context.VaultDocuments.SingleAsync()).OriginalFileName);
        Assert.Null(await context.Transactions.FindAsync("alice-tx"));
    }

    [Fact]
    public void EveryUserOwnedEntity_HasARequiredAppUserForeignKey()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var ownedTypes = context.Model.GetEntityTypes()
            .Where(entity => typeof(IUserOwnedEntity).IsAssignableFrom(entity.ClrType))
            .ToList();

        Assert.NotEmpty(ownedTypes);
        Assert.All(ownedTypes, entity =>
        {
            var userId = entity.FindProperty(nameof(IUserOwnedEntity.UserId));
            Assert.NotNull(userId);
            Assert.False(userId.IsNullable);
            Assert.Contains(entity.GetForeignKeys(), foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(AppUser) &&
                foreignKey.Properties.Count == 1 &&
                foreignKey.Properties[0] == userId);
        });
    }

    [Fact]
    public void EveryUserOwnedEntity_CascadeDeletesWithItsOwner()
    {
        // A deleted AppUser must take all of its owned rows with it, so no orphaned
        // financial/auth data can linger and later be re-associated with a new account.
        using var context = TestHelpers.NewInMemoryContext();
        var ownedTypes = context.Model.GetEntityTypes()
            .Where(entity => typeof(IUserOwnedEntity).IsAssignableFrom(entity.ClrType))
            .ToList();

        Assert.NotEmpty(ownedTypes);
        Assert.All(ownedTypes, entity =>
        {
            var ownerForeignKey = entity.GetForeignKeys().Single(foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(AppUser) &&
                foreignKey.Properties.Count == 1 &&
                foreignKey.Properties[0].Name == nameof(IUserOwnedEntity.UserId));
            Assert.Equal(DeleteBehavior.Cascade, ownerForeignKey.DeleteBehavior);
        });
    }

    [Fact]
    public async Task SaveChanges_StampsTheCurrentUserOnNewOwnedRows()
    {
        // Controllers create rows without setting UserId; the DbContext must stamp the
        // authenticated owner automatically so callers can never forget to scope a write.
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: "alice-user");
        context.Transactions.Add(NewTransactionWithoutOwner("stamped-tx", "Auto stamped"));
        await context.SaveChangesAsync();

        var saved = await context.Transactions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("alice-user", saved.UserId);
    }

    [Fact]
    public async Task SaveChanges_RejectsInsertingARowOwnedByAnotherUser()
    {
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: "alice-user");
        context.Transactions.Add(NewTransaction("foreign-tx", "bob-user", "Belongs to Bob"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("another user", error.Message);
    }

    [Fact]
    public async Task SaveChanges_RejectsModifyingARowOwnedByAnotherUser()
    {
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.Transactions.Add(NewTransaction("bob-tx", "bob-user", "Bob transaction"));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // A request authenticated as Alice loads Bob's row (bypassing the filter) and edits it.
        context.SetCurrentUser("alice-user");
        var bobTransaction = await context.Transactions.IgnoreQueryFilters().SingleAsync();
        bobTransaction.Description = "Tampered by Alice";

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChanges_RejectsDeletingARowOwnedByAnotherUser()
    {
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.Transactions.Add(NewTransaction("bob-tx", "bob-user", "Bob transaction"));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        context.SetCurrentUser("alice-user");
        var bobTransaction = await context.Transactions.IgnoreQueryFilters().SingleAsync();
        context.Transactions.Remove(bobTransaction);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChanges_ThrowsWhenNoOwnerCanBeDetermined()
    {
        // No bound user and no explicit UserId means the row cannot be scoped to anyone.
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.Transactions.Add(NewTransactionWithoutOwner("orphan-tx", "No owner"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChanges_AllowsAnExplicitOwnerWhenNoUserIsBound()
    {
        // Startup seeding (DbSeeder.Seed) runs without a bound user but sets UserId explicitly;
        // that path must remain allowed so defaults can be provisioned for every account.
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.Transactions.Add(NewTransaction("seeded-tx", "seed-user", "Seeded row"));

        await context.SaveChangesAsync();

        Assert.Equal("seed-user", (await context.Transactions.IgnoreQueryFilters().SingleAsync()).UserId);
    }

    [Fact]
    public async Task Queries_ReturnNothingWhenNoUserIsBound()
    {
        // If a request ever reaches the data layer without an authenticated user, the filter
        // must fail closed (return nothing) rather than leak every user's rows.
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.Transactions.Add(NewTransaction("alice-tx", "alice-user", "Alice transaction"));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.Equal(0, await context.Transactions.CountAsync());
        Assert.Equal(1, await context.Transactions.IgnoreQueryFilters().CountAsync());
    }

    private static Transaction NewTransaction(string id, string userId, string description) => new()
    {
        Id = id,
        UserId = userId,
        Date = DateTime.UtcNow,
        Description = description,
        Category = "Food",
        LedgerCategory = "Essentials",
        Amount = -1
    };

    // Mirrors how a controller builds a row: the owner is left for the DbContext to stamp.
    private static Transaction NewTransactionWithoutOwner(string id, string description) => new()
    {
        Id = id,
        Date = DateTime.UtcNow,
        Description = description,
        Category = "Food",
        LedgerCategory = "Essentials",
        Amount = -1
    };

    private static RecurringPayment NewRecurringPayment(string id, string userId, string name) => new()
    {
        Id = id,
        UserId = userId,
        Name = name,
        Amount = -1,
        Frequency = "Monthly",
        Category = "Food",
        LedgerCategory = "Essentials",
        NextDueDate = "2026-08-01",
        DueDate = 1,
        StartDate = "2026-01-01",
        Active = true
    };
}
