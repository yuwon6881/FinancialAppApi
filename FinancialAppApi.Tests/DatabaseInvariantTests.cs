using FinancialAppApi.Models;

namespace FinancialAppApi.Tests;

public class DatabaseInvariantTests
{
    [Fact]
    public void Model_EnforcesOneApplicationUser()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var entity = context.Model.FindEntityType(typeof(AppUser))!;

        var index = Assert.Single(entity.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(AppUser.SingletonKey)]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Model_EnforcesOneTransactionPerWishlistPurchase()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var entity = context.Model.FindEntityType(typeof(Transaction))!;

        var index = Assert.Single(entity.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(Transaction.WishlistItemId)]));

        Assert.True(index.IsUnique);
    }
}
