using FinancialAppApi.Database;

namespace FinancialAppApi.Services.Accounts;

public static class LedgerAccountPlacement
{
    public static bool IsBucket(string? value) =>
        BucketOrNull(value) is not null;

    public static string? BucketOrNull(string? value) =>
        value is null
            ? null
            : FinancialConstants.BudgetCategories.FirstOrDefault(
                bucket => bucket.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
}
