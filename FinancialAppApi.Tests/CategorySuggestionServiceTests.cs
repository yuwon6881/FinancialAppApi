using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public class CategorySuggestionServiceTests
{
    [Fact]
    public void ParseSuggestions_ReturnsTopThreeCanonicalCategories()
    {
        var result = CategorySuggestionService.ParseSuggestions(
            """
            {
              "suggestions": [
                { "category": "food", "confidence": 0.93 },
                { "category": "Transport", "confidence": 0.65 },
                { "category": "Other", "confidence": 0.2 },
                { "category": "Software", "confidence": 0.8 }
              ]
            }
            """,
            ["Food", "Transport", "Software", "Other"]);

        Assert.Equal(["Food", "Software", "Transport"], result.Select(s => s.Category).ToArray());
        Assert.Equal(0.93, result[0].Confidence, 3);
    }

    [Fact]
    public void ParseSuggestions_RejectsUnknownAndDuplicateCategories()
    {
        var result = CategorySuggestionService.ParseSuggestions(
            """
            {
              "suggestions": [
                { "category": "Made Up", "confidence": 0.99 },
                { "category": "Food", "confidence": 99 },
                { "category": "food", "confidence": 0.1 }
              ]
            }
            """,
            ["Food"]);

        var suggestion = Assert.Single(result);
        Assert.Equal("Food", suggestion.Category);
        Assert.Equal(0.99, suggestion.Confidence, 3);
    }
}
