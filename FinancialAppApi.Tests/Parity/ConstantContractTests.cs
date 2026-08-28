using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Investments;
using FinancialAppApi.Contracts;

namespace FinancialAppApi.Tests.Parity;

public sealed class ConstantContractTests
{
    [Fact]
    public void BackendStringContractsMatchCanonicalFixture()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var fixtureSets = document.RootElement.GetProperty("sets").EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.EnumerateArray().Select(value => value.GetString()!).ToArray());

        var backendSets = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["LoanInterestMethod"] = [
                LoanInterestMethod.ReducingBalance,
                LoanInterestMethod.ReducingBalanceDaily,
                LoanInterestMethod.Flat,
                LoanInterestMethod.InterestOnly],
            ["LoanRateBasis"] = [LoanRateBasis.Yearly, LoanRateBasis.Monthly],
            ["RecurringPaymentMode"] = [RecurringPaymentMode.AutoDeduct, RecurringPaymentMode.Manual],
            ["CategoryFlowType"] = [CategoryFlowType.Both, CategoryFlowType.Inflow, CategoryFlowType.Outflow],
            ["SavingsGoalStatus"] = [SavingsGoalStatus.Active, SavingsGoalStatus.Completed],
            ["StabilityReloadIntent"] = [
                StabilityReloadIntent.Unanswered,
                StabilityReloadIntent.Required,
                StabilityReloadIntent.NotRequired],
            ["StabilityOverflowRedirect"] = StabilityOverflowRedirectOptions.All,
            ["FundingBucket"] = [SavingsGoalFundingBucket.Essentials, SavingsGoalFundingBucket.Rewards],
            ["LedgerBucket"] = FinancialConstants.BudgetCategories,
            ["PushChannel"] = [PushChannel.BillReminders, PushChannel.CategoryAlerts],
            ["InvestmentChartRange"] = InvestmentChartRange.Allowed,
            ["RefreshSlice"] = RefreshSliceNames.All,
            ["RefreshHeader"] = [RefreshSliceNames.HeaderName],
        };

        Assert.True(backendSets.Keys.Order().SequenceEqual(fixtureSets.Keys.Order()));
        foreach (var (name, values) in backendSets)
        {
            Assert.True(fixtureSets[name].Order().SequenceEqual(values.Order()), name);
            Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        }
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Parity", "Fixtures", "constants.cases.json");
}
