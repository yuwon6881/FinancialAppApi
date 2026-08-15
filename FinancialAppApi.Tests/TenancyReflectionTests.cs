using System.Collections;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class TenancyReflectionTests
{
    private static readonly HashSet<Type> PreAuthExplicitScopeTypes =
    [
        typeof(UserSession),
        typeof(WebAuthnCredential),
        typeof(WebAuthnChallenge),
        typeof(ReceiptScanJob),
        typeof(PendingTwoFactor),
        typeof(RecoveryCode),
        typeof(SecurityQuestionAnswer),
    ];

    [Fact]
    public void EveryUserOwnedEntity_IsInTheModelAndHasTheExpectedTenantContract()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var modelTypes = context.Model.GetEntityTypes()
            .ToDictionary(entity => entity.ClrType);
        var ownedTypes = UserOwnedEntityTypes();

        Assert.NotEmpty(ownedTypes);
        foreach (var clrType in ownedTypes)
        {
            Assert.True(modelTypes.TryGetValue(clrType, out var entity),
                $"{clrType.Name} implements IUserOwnedEntity but is not in AppDbContext's model.");

            var userId = entity!.FindProperty(nameof(IUserOwnedEntity.UserId));
            Assert.NotNull(userId);
            Assert.False(userId!.IsNullable);
            Assert.Contains(entity.GetForeignKeys(), foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(AppUser) &&
                foreignKey.Properties.Count == 1 &&
                foreignKey.Properties[0] == userId);

            if (PreAuthExplicitScopeTypes.Contains(clrType))
            {
                Assert.Empty(entity.GetDeclaredQueryFilters());
            }
            else
            {
                Assert.NotEmpty(entity.GetDeclaredQueryFilters());
            }
        }
    }

    [Fact]
    public async Task EveryUserOwnedEntity_RejectsAWriteForAnotherCurrentUser()
    {
        foreach (var clrType in UserOwnedEntityTypes())
        {
            await using var context = TestHelpers.NewInMemoryContext(currentUserId: "alice-user");
            var row = Activator.CreateInstance(clrType);
            Assert.NotNull(row);
            clrType.GetProperty(nameof(IUserOwnedEntity.UserId))!.SetValue(row, "bob-user");
            context.Add(row!);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
            Assert.Contains("another user", error.Message);
        }
    }

    [Fact]
    public async Task EveryFilteredUserOwnedEntity_HidesTheOtherUsersRows()
    {
        var filteredTypes = UserOwnedEntityTypes()
            .Where(type => !PreAuthExplicitScopeTypes.Contains(type))
            .ToList();

        foreach (var clrType in filteredTypes)
        {
            await using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
            context.AppUsers.AddRange(
                new AppUser { Id = "alice-user", Username = "alice", PasswordHash = "hash" },
                new AppUser { Id = "bob-user", Username = "bob", PasswordHash = "hash" });

            context.Add(CreateOwnedRow(context, clrType, "alice-user", "alice"));
            context.Add(CreateOwnedRow(context, clrType, "bob-user", "bob"));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            context.SetCurrentUser("alice-user");
            var setMethod = typeof(DbContext).GetMethods()
                .Single(method => method.Name == nameof(DbContext.Set)
                    && method.IsGenericMethodDefinition
                    && method.GetParameters().Length == 0)
                .MakeGenericMethod(clrType);
            var visibleRows = ((IEnumerable)setMethod.Invoke(context, null)!)
                .Cast<IUserOwnedEntity>()
                .ToList();

            Assert.Single(visibleRows);
            Assert.Equal("alice-user", visibleRows[0].UserId);
        }
    }

    private static Type[] UserOwnedEntityTypes() =>
        typeof(IUserOwnedEntity).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                           typeof(IUserOwnedEntity).IsAssignableFrom(type))
            .OrderBy(type => type.FullName)
            .ToArray();

    private static object CreateOwnedRow(AppDbContext context, Type clrType, string userId, string suffix)
    {
        var row = Activator.CreateInstance(clrType)!;
        clrType.GetProperty(nameof(IUserOwnedEntity.UserId))!.SetValue(row, userId);

        var keyProperties = context.Model.FindEntityType(clrType)?.FindPrimaryKey()?.Properties ?? [];
        foreach (var keyProperty in keyProperties)
        {
            if (keyProperty.Name == nameof(IUserOwnedEntity.UserId))
                continue;

            var property = clrType.GetProperty(keyProperty.Name);
            if (property?.CanWrite != true)
                continue;

            object? value = keyProperty.ClrType switch
            {
                var type when type == typeof(string) => $"{clrType.Name}-{suffix}",
                var type when type == typeof(int) => suffix == "alice" ? 1 : 2,
                var type when type == typeof(long) => suffix == "alice" ? 1L : 2L,
                var type when type == typeof(Guid) => suffix == "alice"
                    ? Guid.Parse("00000000-0000-0000-0000-000000000001")
                    : Guid.Parse("00000000-0000-0000-0000-000000000002"),
                var type when type == typeof(DateOnly) => suffix == "alice"
                    ? new DateOnly(2020, 1, 1)
                    : new DateOnly(2020, 1, 2),
                var type when type == typeof(DateTime) => suffix == "alice"
                    ? new DateTime(2020, 1, 1)
                    : new DateTime(2020, 1, 2),
                _ => null,
            };
            if (value is not null)
                property.SetValue(row, value);
        }

        return row;
    }
}
