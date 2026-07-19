using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

public static class SecurityQuestionNormalization
{
    public static string NormalizeAnswer(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 1. Lowercase
        var normalized = input.ToLowerInvariant();

        // 2. Remove all punctuation and special characters
        normalized = Regex.Replace(normalized, @"[^\w\s]", "");

        // 3. Replace multiple spaces with a single space, and trim
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        // Optional: you could just remove all spaces entirely if you want to be even more lenient:
        // normalized = Regex.Replace(normalized, @"\s+", "");

        return normalized;
    }
}
