using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public sealed class CurrencyCatalogTests
{
    [Theory]
    [InlineData("USD")]
    [InlineData("MYR")]
    [InlineData("SGD")]
    [InlineData("CNY")]
    [InlineData("EUR")]
    [InlineData("GBP")]
    [InlineData("XPF")]
    [InlineData("ZWG")]
    public void Contains_RecognizesSupportedActiveCodes(string code)
    {
        Assert.True(CurrencyCatalog.Contains(code));
        Assert.Single(CurrencyCatalog.Items, item => item.Code == code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("ZZZ")]
    [InlineData("BTC")]
    public void Contains_RejectsUnknownCodes(string code) =>
        Assert.False(CurrencyCatalog.Contains(code));

    [Fact]
    public void Items_AreDeterministicUniqueAndOrdinallySorted()
    {
        Assert.Equal(CurrencyCatalog.Items.Count, CurrencyCatalog.Items.Select(item => item.Code).Distinct().Count());
        Assert.Equal(
            CurrencyCatalog.Items.Select(item => item.Code).OrderBy(code => code, StringComparer.Ordinal),
            CurrencyCatalog.Items.Select(item => item.Code));
    }
}
