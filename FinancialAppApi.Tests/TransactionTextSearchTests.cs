using System.Text.RegularExpressions;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public class TransactionTextSearchTests
{
    // The in-memory provider evaluates the whole-word predicate with .NET's regex engine, so only
    // an explicit check keeps the PostgreSQL spelling honest: `\p{L}` is not valid in POSIX ARE
    // and made the deployed all-cycles whole-word search fail with "invalid regular expression".
    [Fact]
    public void WholeWordPattern_UsesPosixClassesForPostgres()
    {
        var pattern = TransactionTextSearch.WholeWordPattern("coffee bean", forPostgres: true);

        Assert.DoesNotContain(@"\p{", pattern, StringComparison.Ordinal);
        Assert.StartsWith("(?<![[:alnum:]])", pattern, StringComparison.Ordinal);
        Assert.EndsWith("(?![[:alnum:]])", pattern, StringComparison.Ordinal);
    }

    [Fact]
    public void WholeWordPattern_MatchesWholeWordsOnlyWithTheDotNetSpelling()
    {
        var pattern = TransactionTextSearch.WholeWordPattern("bean", forPostgres: false);

        Assert.True(Regex.IsMatch("Iced Coffee Bean Latte", pattern, RegexOptions.IgnoreCase));
        Assert.True(Regex.IsMatch("rice/bean-mix", pattern, RegexOptions.IgnoreCase));
        Assert.False(Regex.IsMatch("Coffee Beans", pattern, RegexOptions.IgnoreCase));
    }

    [Fact]
    public void WholeWordPattern_TreatsPunctuationInTheTermAsLiteralText()
    {
        var pattern = TransactionTextSearch.WholeWordPattern("cafe (special)", forPostgres: false);

        Assert.True(Regex.IsMatch("Cafe (special)", pattern, RegexOptions.IgnoreCase));
        Assert.False(Regex.IsMatch("Cafe special", pattern, RegexOptions.IgnoreCase));
    }
}
