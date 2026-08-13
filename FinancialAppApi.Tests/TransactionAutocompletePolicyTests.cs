using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public class TransactionAutocompletePolicyTests
{
    [Theory]
    [InlineData("Account balance alignment - RYT", "Adjustment", "Essentials", null, null, true)]
    [InlineData("Any description", "Transfer", "Essentials", null, null, true)]
    [InlineData("Any description", "Food", "AccountMove", null, null, true)]
    [InlineData("Any description", "Food", "Transfer:Income->Rewards", null, null, true)]
    [InlineData("Any description", "Food", "Discarded", null, null, true)]
    [InlineData("Any description", "Food", "Essentials", null, null, false)]
    [InlineData("Account balance alignment - RYT", "Food", "Essentials", null, null, false)]
    [InlineData("tx-split-Essentials", "Food", "Essentials", null, null, true)]
    [InlineData("Any description", "Food", "Essentials", 42, null, true)]
    [InlineData("Any description", "Food", "Essentials", null, 7, true)]
    public void ShouldExclude_IsStructuralAndDoesNotMatchDescription(
        string id,
        string category,
        string ledgerCategory,
        int? wishlistItemId,
        int? savingsGoalId,
        bool expected)
    {
        Assert.Equal(
            expected,
            TransactionAutocompletePolicy.ShouldExclude(
                id, category, ledgerCategory, wishlistItemId, savingsGoalId));
    }
}
