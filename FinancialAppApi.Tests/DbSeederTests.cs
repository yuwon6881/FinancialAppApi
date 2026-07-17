using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class DbSeederTests
{
    private const string UserId = "seed-user";

    [Fact]
    public void EnsureUserDefaults_SeedsAllDefaultCategories_ForNewUser()
    {
        using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.AppUsers.Add(new AppUser { Id = UserId, Username = "seed", NormalizedUsername = "SEED" });
        context.SaveChanges();

        DbSeeder.EnsureUserDefaults(context, UserId);
        context.SaveChanges();

        var names = context.TransactionCategories
            .IgnoreQueryFilters()
            .Where(c => c.UserId == UserId)
            .Select(c => c.Name)
            .ToList();
        Assert.Equal(10, names.Count);
        Assert.Contains("Social", names);
    }

    [Fact]
    public void EnsureUserDefaults_DoesNotResurrectDeletedDefault_OnReSeed()
    {
        using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.AppUsers.Add(new AppUser { Id = UserId, Username = "seed", NormalizedUsername = "SEED" });
        context.SaveChanges();

        // Initial provisioning.
        DbSeeder.EnsureUserDefaults(context, UserId);
        context.SaveChanges();

        // User deletes a default category (hard delete, as the app does).
        var social = context.TransactionCategories
            .IgnoreQueryFilters()
            .Single(c => c.UserId == UserId && c.Name == "Social");
        context.TransactionCategories.Remove(social);
        context.SaveChanges();

        // A subsequent deployment re-runs seeding for every existing user.
        DbSeeder.EnsureUserDefaults(context, UserId);
        context.SaveChanges();

        var names = context.TransactionCategories
            .IgnoreQueryFilters()
            .Where(c => c.UserId == UserId)
            .Select(c => c.Name)
            .ToList();
        Assert.DoesNotContain("Social", names);
        Assert.Equal(9, names.Count);
    }

    [Fact]
    public void EnsureUserDefaults_DoesNotDuplicateCategories_OnReSeed()
    {
        using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.AppUsers.Add(new AppUser { Id = UserId, Username = "seed", NormalizedUsername = "SEED" });
        context.SaveChanges();

        DbSeeder.EnsureUserDefaults(context, UserId);
        context.SaveChanges();
        DbSeeder.EnsureUserDefaults(context, UserId);
        context.SaveChanges();

        var count = context.TransactionCategories
            .IgnoreQueryFilters()
            .Count(c => c.UserId == UserId);
        Assert.Equal(10, count);
    }
}
