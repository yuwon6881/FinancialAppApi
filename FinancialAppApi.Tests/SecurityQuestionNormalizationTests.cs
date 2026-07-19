using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public class SecurityQuestionNormalizationTests
{
    [Theory]
    [InlineData("My Answer", "my answer")]
    [InlineData("  My   Answer  ", "my answer")]
    [InlineData("My-Answer!", "myanswer")]
    [InlineData("123 My Answer 123", "123 my answer 123")]
    [InlineData("O'Connor", "oconnor")]
    [InlineData("São Paulo", "são paulo")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_HandlesVariousInputs_Correctly(string? input, string expected)
    {
        // Act
        var result = SecurityQuestionNormalization.NormalizeAnswer(input ?? string.Empty);

        // Assert
        Assert.Equal(expected, result);
    }
}
