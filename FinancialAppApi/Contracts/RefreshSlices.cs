using FinancialAppApi.Services.Documents;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Contracts;

/// <summary>
/// The additive refresh contract shared by mutation responses and the partial bootstrap endpoint.
/// Keep this list in lockstep with the frontend mirror and the root parity fixture.
/// </summary>
public static class RefreshSliceNames
{
    public const string HeaderName = "X-FinancialApp-Refresh-Slices";
    public const string Core = "core";
    public const string Recurring = "recurring";
    public const string Categories = "categories";
    public const string Wishlist = "wishlist";
    public const string SavingsGoals = "savingsGoals";
    public const string Loans = "loans";
    public const string Investments = "investments";
    public const string Documents = "documents";

    public static readonly IReadOnlyList<string> All =
    [Core, Recurring, Categories, Wishlist, SavingsGoals, Loans, Investments, Documents];

    public static bool IsKnown(string value) =>
        All.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// Parses a request query without accepting duplicate, empty, or unknown values. The special
    /// <c>all</c> response marker is intentionally not a valid partial request.
    /// </summary>
    public static bool TryParseRequest(
        string? raw,
        out IReadOnlyList<string> slices,
        out string? error)
    {
        slices = [];
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "At least one refresh slice is required.";
            return false;
        }

        var values = raw.Split(',', StringSplitOptions.None)
            .Select(value => value.Trim())
            .ToArray();
        if (values.Any(value => value.Length == 0))
        {
            error = "Refresh slices must not be empty.";
            return false;
        }
        if (values.Length != values.Distinct(StringComparer.Ordinal).Count())
        {
            error = "Refresh slices must not contain duplicates.";
            return false;
        }
        var unknown = values.FirstOrDefault(value => !IsKnown(value));
        if (unknown is not null)
        {
            error = $"Unknown refresh slice '{unknown}'.";
            return false;
        }

        slices = values;
        return true;
    }
}

/// <summary>
/// The investment slice is the authoritative allocation read model used by the shell and the
/// Settings allocation section. The portfolio page may still request its selected chart range
/// when it is mounted; keeping that range-specific payload out of this refresh avoids silently
/// replacing a 1-year chart with the default 3-month chart.
/// </summary>
public sealed record BootstrapInvestmentRefreshDto(
    InvestmentAllocationOverviewDto Allocation);

/// <summary>Shared document read models needed to reconcile a mounted Vault.</summary>
public sealed record BootstrapDocumentsRefreshDto(
    DocumentVaultUsage Usage,
    IReadOnlyList<int> AvailableYears,
    DocumentRetentionReview Retention,
    int? SelectedTaxYear,
    TaxYearReliefSummary? Summary,
    IReadOnlyList<TaxReliefCategoryDefinition> ReliefCategories);
