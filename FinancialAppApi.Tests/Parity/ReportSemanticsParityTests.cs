using System.Text.Json;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests.Parity;

public sealed class ReportSemanticsParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [item.Clone()];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReportSemanticsMatchesCanonicalFixture(JsonElement item)
    {
        var id = item.GetProperty("id").GetString()!;
        var why = item.GetProperty("why").GetString()!;
        var input = item.GetProperty("input");
        var amount = input.GetProperty("amount").GetDecimal();
        var category = input.GetProperty("category").GetString()!;
        var ledgerCategory = input.GetProperty("ledgerCategory").GetString()!;

        var expected = item.GetProperty("expected");

        Assert.Equal(expected.GetProperty("isTransfer").GetBoolean(), TransactionReportSemantics.IsTransfer(category, ledgerCategory));
        Assert.Equal(expected.GetProperty("isBalanceAdjustment").GetBoolean(), TransactionReportSemantics.IsBalanceAdjustment(category));
        Assert.Equal(expected.GetProperty("isReportableCashMovement").GetBoolean(), TransactionReportSemantics.IsReportableCashMovement(amount, category, ledgerCategory));
        Assert.Equal(expected.GetProperty("isReportableIncome").GetBoolean(), TransactionReportSemantics.IsReportableIncome(amount, category, ledgerCategory));
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Parity", "Fixtures", "report-semantics.cases.json");
}
