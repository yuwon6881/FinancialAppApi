using System.Text.Json;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class CategorySuggestionService
{
    public static IReadOnlyList<CategorySuggestion> ParseSuggestions(string text, IReadOnlyList<string> categoryNames)
    {
        text = StripMarkdownFence(text.Trim());

        using var resultDoc = JsonDocument.Parse(text);
        var root = resultDoc.RootElement;
        var suggestionsElement = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("suggestions", out var suggestionsProp)
                ? suggestionsProp
                : default;

        if (suggestionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var categoryByName = categoryNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(name => name.Trim().ToLowerInvariant(), name => name.Trim());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<CategorySuggestion>();

        foreach (var item in suggestionsElement.EnumerateArray())
        {
            if (!item.TryGetProperty("category", out var categoryProp))
            {
                continue;
            }

            var rawCategory = categoryProp.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(rawCategory) ||
                !categoryByName.TryGetValue(rawCategory.ToLowerInvariant(), out var canonicalCategory) ||
                !seen.Add(canonicalCategory))
            {
                continue;
            }

            var confidence = 0.5;
            if (item.TryGetProperty("confidence", out var confidenceProp) &&
                confidenceProp.ValueKind == JsonValueKind.Number)
            {
                confidence = confidenceProp.GetDouble();
                if (confidence > 1.0)
                {
                    confidence /= 100.0;
                }
            }

            confidence = Math.Clamp(confidence, 0.0, 1.0);
            suggestions.Add(new CategorySuggestion(canonicalCategory, confidence));
        }

        return suggestions
            .OrderByDescending(s => s.Confidence)
            .Take(3)
            .ToList();
    }

    private static IReadOnlyList<TransactionNoteSuggestion> ParseNoteSuggestions(string text)
    {
        text = StripMarkdownFence(text.Trim());

        using var resultDoc = JsonDocument.Parse(text);
        var root = resultDoc.RootElement;
        var notesElement = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("notes", out var notesProp)
                ? notesProp
                : default;

        if (notesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<TransactionNoteSuggestion>();

        foreach (var item in notesElement.EnumerateArray())
        {
            var note = item.TryGetProperty("note", out var noteProp)
                ? noteProp.GetString()?.Trim()
                : item.ValueKind == JsonValueKind.String
                    ? item.GetString()?.Trim()
                    : null;

            if (string.IsNullOrWhiteSpace(note)) continue;
            note = note.Length > 90 ? note[..90].Trim() : note;
            if (!seen.Add(note)) continue;

            var reason = item.TryGetProperty("reason", out var reasonProp)
                ? reasonProp.GetString()?.Trim()
                : "Suggested by AI";

            notes.Add(new TransactionNoteSuggestion(note, string.IsNullOrWhiteSpace(reason) ? "Suggested by AI" : reason!));
        }

        return notes.Take(3).ToList();
    }

    private static CategoryCleanupReview ParseCategoryCleanupReview(
        string text,
        IReadOnlyList<CategoryReviewCategory> categoryDetails,
        IReadOnlyList<CategoryReviewTransaction> recentTransactions)
    {
        text = StripMarkdownFence(text.Trim());

        using var resultDoc = JsonDocument.Parse(text);
        var root = resultDoc.RootElement;
        var suggestionsElement = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("suggestions", out var suggestionsProp)
                ? suggestionsProp
                : default;

        if (suggestionsElement.ValueKind != JsonValueKind.Array)
        {
            return new CategoryCleanupReview([]);
        }

        var canonicalByName = categoryDetails.ToDictionary(c => c.Name.ToLowerInvariant(), c => c.Name);
        var flowByName = categoryDetails.ToDictionary(c => c.Name, c => c.Flow, StringComparer.OrdinalIgnoreCase);
        var existingLower = canonicalByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<CategoryCleanupSuggestion>();

        foreach (var item in suggestionsElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeProp)
                ? typeProp.GetString()?.Trim().ToLowerInvariant()
                : null;
            if (type is not ("delete" or "merge" or "add" or "consolidate" or "changeflow")) continue;

            var categories = new List<string>();
            if (item.TryGetProperty("categories", out var categoriesProp) && categoriesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var catProp in categoriesProp.EnumerateArray())
                {
                    var raw = catProp.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(raw) &&
                        canonicalByName.TryGetValue(raw.ToLowerInvariant(), out var canonical) &&
                        !categories.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                    {
                        categories.Add(canonical);
                    }
                }
            }

            var targetCategory = item.TryGetProperty("targetCategory", out var targetProp)
                ? targetProp.GetString()?.Trim()
                : null;
            if (!string.IsNullOrWhiteSpace(targetCategory) &&
                canonicalByName.TryGetValue(targetCategory.ToLowerInvariant(), out var canonicalTarget))
            {
                targetCategory = canonicalTarget;
            }
            else
            {
                targetCategory = null;
            }

            var newCategoryName = item.TryGetProperty("newCategoryName", out var newProp)
                ? CleanCategoryName(newProp.GetString())
                : null;
            var sourceFlow = item.TryGetProperty("sourceFlow", out var sourceFlowProp)
                ? CategoryFlowType.Normalize(sourceFlowProp.GetString())
                : null;
            var targetFlow = item.TryGetProperty("targetFlow", out var targetFlowProp) &&
                             CategoryFlowType.IsValid(targetFlowProp.GetString())
                ? CategoryFlowType.Normalize(targetFlowProp.GetString())
                : null;

            // The user picks the destination manually for a low-usage cleanup, so the AI is
            // never trusted to name a target here even if it tried to include one.
            if (type == "consolidate")
            {
                targetCategory = null;
            }

            if (type == "changeflow")
            {
                if (categories.Count != 1 || targetFlow == null || !flowByName.TryGetValue(categories[0], out var currentFlow)) continue;
                sourceFlow = currentFlow;
                if (string.Equals(sourceFlow, targetFlow, StringComparison.Ordinal)) continue;
            }

            if (type == "delete" && categories.Count == 0) continue;
            if (type == "merge" && (categories.Count == 0 || targetCategory == null)) continue;
            if (type == "consolidate" && categories.Count != 1) continue;
            if (type == "changeflow" && categories.Count != 1) continue;
            // A reserved name is filtered out of the list the model sees, so it does not look
            // taken -- and the applier silently refuses it, leaving an accepted suggestion that
            // did nothing. Drop it during review instead.
            if (type == "add" && (newCategoryName == null ||
                existingLower.Contains(newCategoryName.ToLowerInvariant()) ||
                TransactionCategoryService.IsReservedName(newCategoryName))) continue;

            var affectedCount = type switch
            {
                "add" => 0,
                "changeflow" when targetFlow == CategoryFlowType.Inflow => recentTransactions.Count(t =>
                    categories.Contains(t.Category, StringComparer.OrdinalIgnoreCase) && t.Amount < 0),
                "changeflow" when targetFlow == CategoryFlowType.Outflow => recentTransactions.Count(t =>
                    categories.Contains(t.Category, StringComparer.OrdinalIgnoreCase) && t.Amount > 0),
                "changeflow" => 0,
                _ => recentTransactions.Count(t => categories.Contains(t.Category, StringComparer.OrdinalIgnoreCase))
            };

            // A merge folds a whole category's history into another one, so it must be backed by
            // more than a single coincidentally-overlapping transaction -- that case belongs to
            // "consolidate" instead, where the user picks the destination themselves.
            if (type == "merge" && affectedCount <= 1) continue;

            var confidence = 0.5;
            if (item.TryGetProperty("confidence", out var confidenceProp) && confidenceProp.ValueKind == JsonValueKind.Number)
            {
                confidence = confidenceProp.GetDouble();
                if (confidence > 1.0) confidence /= 100.0;
            }
            if (type == "changeflow" && confidence < 0.8) continue;

            var title = item.TryGetProperty("title", out var titleProp)
                ? titleProp.GetString()?.Trim()
                : null;
            var summary = item.TryGetProperty("summary", out var summaryProp)
                ? summaryProp.GetString()?.Trim()
                : null;

            suggestions.Add(new CategoryCleanupSuggestion(
                $"cleanup-{suggestions.Count + 1}",
                type == "changeflow" ? "changeFlow" : type,
                string.IsNullOrWhiteSpace(title) ? DefaultCleanupTitle(type, categories, targetCategory, newCategoryName) : title!,
                string.IsNullOrWhiteSpace(summary) ? "AI found a category cleanup opportunity." : summary!,
                categories,
                targetCategory,
                newCategoryName,
                affectedCount,
                Math.Clamp(confidence, 0.0, 1.0),
                sourceFlow,
                targetFlow));
        }

        return new CategoryCleanupReview(suggestions.Take(5).ToList());
    }

    private static string DefaultCleanupTitle(string type, IReadOnlyList<string> categories, string? targetCategory, string? newCategoryName)
    {
        return type switch
        {
            "delete" => $"Remove {string.Join(", ", categories)}",
            "merge" => $"Merge into {targetCategory}",
            "consolidate" => $"Consolidate {string.Join(", ", categories)} (low usage)",
            "add" => $"Add {newCategoryName}",
            "changeflow" => $"Correct {string.Join(", ", categories)} flow",
            _ => "Review category"
        };
    }

    private static IEnumerable<string> NormalizeCategoryNames(IEnumerable<string>? requestedCategories)
    {
        return requestedCategories?
            .Select(c => c.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .Take(100) ?? [];
    }

    // Shared with CategoryCleanupApplier, whose apply/undo actions must normalize
    // category names exactly the way the review parser does.
    internal static string? CleanCategoryName(string? name)
    {
        var cleaned = name?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return null;
        return cleaned.Length > 100 ? cleaned[..100].Trim() : cleaned;
    }

    private static string StripMarkdownFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewline > 0 && lastFence > firstNewline)
        {
            return text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
        }

        return text;
    }
}
