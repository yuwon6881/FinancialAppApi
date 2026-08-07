using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using FinancialAppApi.Diagnostics;

namespace FinancialAppApi.Services;

public sealed record CategorySuggestion(string Category, double Confidence);
public sealed record TransactionNoteSuggestion(string Note, string Reason);
public sealed record CategoryCleanupSuggestion(
    string Id,
    string Type,
    string Title,
    string Summary,
    IReadOnlyList<string> Categories,
    string? TargetCategory,
    string? NewCategoryName,
    int AffectedTransactionCount,
    double Confidence);

public sealed record CategoryCleanupReview(IReadOnlyList<CategoryCleanupSuggestion> Suggestions);
public sealed record CategoryCleanupAction(
    string Type,
    IReadOnlyList<string>? Categories = null,
    string? TargetCategory = null,
    string? NewCategoryName = null,
    IReadOnlyList<string>? TransactionIds = null,
    IReadOnlyList<string>? RecurringPaymentIds = null,
    string? CategoryId = null);

public sealed record CategoryCleanupApplyResult(int AppliedCount, IReadOnlyList<CategoryCleanupAction> UndoActions);

// Same Status-plus-message idea as CategoryCleanupApplyResult's sibling below, but for the
// apply path specifically: a conflict (category now in use) aborts the whole transaction, so
// the controller needs to tell "applied" apart from "rolled back" without a thrown exception.
public sealed record CategoryCleanupApplyOutcome(CategoryCleanupApplyResult? Result, string? ConflictMessage)
{
    public static CategoryCleanupApplyOutcome Ok(CategoryCleanupApplyResult result) => new(result, null);
    public static CategoryCleanupApplyOutcome Conflict(string message) => new(null, message);
}

// Mirrors the Status-enum-plus-message pattern the rest of the app uses for expected
// failures (e.g. CreateTransactionCategoryResult/DeleteTransactionCategoryResult) instead
// of exceptions, so the controller can switch on a result the same way it does for every
// non-AI endpoint.
public enum AiOperationStatus { Ok, Unavailable }

public sealed record AiOperationResult<T>(AiOperationStatus Status, T? Data, string? Message = null)
{
    public static AiOperationResult<T> Ok(T data) => new(AiOperationStatus.Ok, data);
    public static AiOperationResult<T> Failed(string message) => new(AiOperationStatus.Unavailable, default, message);
}

public sealed class CategorySuggestionService
{
    private sealed record CategoryReviewTransaction(string Id, string Description, string Category, string LedgerCategory);

    // Category suggestions run at temperature 0.0 against a fixed (description, txType,
    // categories) input, so an identical call is expected to produce an identical answer --
    // caching it briefly avoids paying for a repeat AI call when a user revisits/re-blurs
    // the same description field, which is common while filling out a transaction form.
    private const string SuggestCachePrefix = "ai-category-suggest:";
    private static readonly TimeSpan SuggestCacheTtl = TimeSpan.FromMinutes(15);
    private const string CleanupCachePrefix = "ai-category-cleanup:";
    private static readonly TimeSpan CleanupCacheTtl = TimeSpan.FromMinutes(10);

    private readonly AiClient _aiClient;
    private readonly AppDbContext _context;
    private readonly TransactionCategoryService _categoryService;
    private readonly IMemoryCache _cache;
    private readonly CategoryCleanupApplier _cleanupApplier;

    public CategorySuggestionService(
        AiClient aiClient,
        AppDbContext context,
        TransactionCategoryService categoryService,
        IMemoryCache cache,
        CategoryCleanupApplier cleanupApplier)
    {
        _aiClient = aiClient;
        _context = context;
        _categoryService = categoryService;
        _cache = cache;
        _cleanupApplier = cleanupApplier;
    }

    public async Task<AiOperationResult<IReadOnlyList<CategorySuggestion>>> SuggestAsync(
        string description,
        string? txType,
        IEnumerable<string>? requestedCategories,
        CancellationToken cancellationToken = default)
    {
        // Categories are authoritative server data. The client list is retained in the API
        // shape for compatibility, but cannot expand or inject text into the model's choices.
        var safeTxType = string.Equals(txType, "inflow", StringComparison.OrdinalIgnoreCase)
            ? "inflow"
            : "outflow";
        var requested = NormalizeCategoryNames(requestedCategories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dbCategories = await _categoryService.GetCategoriesAsync(cancellationToken);
        var matchingCategories = dbCategories
            .Where(c => c.Type == CategoryFlowType.Both || c.Type == safeTxType || string.IsNullOrEmpty(c.Type))
            .ToList();
        var allCategoryNames = matchingCategories
            .Select(c => c.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
            .ToList();
        var requestedServerCategories = allCategoryNames.Where(requested.Contains).ToList();
        var categoryNames = requestedServerCategories.Count > 0 ? requestedServerCategories : allCategoryNames;

        var cacheKey = SuggestCachePrefix + _context.RequireCurrentUserId() + ":" + string.Join('|', [
            description.Trim().ToLowerInvariant(),
            safeTxType,
            string.Join(',', categoryNames.Select(c => c.ToLowerInvariant()))
        ]);
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<CategorySuggestion>? cached) && cached != null)
        {
            Telemetry.CacheHitsCounter.Add(1, new KeyValuePair<string, object?>("cache_type", "ai-category-suggest"));
            return AiOperationResult<IReadOnlyList<CategorySuggestion>>.Ok(cached);
        }
        Telemetry.CacheMissesCounter.Add(1, new KeyValuePair<string, object?>("cache_type", "ai-category-suggest"));

        var normalizedDescription = description.Trim().ToLowerInvariant();
        var historicalCategories = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.LedgerCategory != "Discarded" && t.Description.ToLower() == normalizedDescription)
            .GroupBy(t => t.Category)
            .Select(group => new { Category = group.Key, Count = group.Count() })
            .OrderByDescending(group => group.Count)
            .Take(3)
            .ToListAsync(cancellationToken);
        var historicalSuggestions = historicalCategories
            .Select(match => categoryNames.FirstOrDefault(c => string.Equals(c, match.Category, StringComparison.OrdinalIgnoreCase)))
            .Where(category => category != null)
            .Select((category, index) => new CategorySuggestion(category!, Math.Max(0.90, 0.99 - index * 0.04)))
            .ToList();
        if (historicalSuggestions.Count > 0)
        {
            _cache.Set(cacheKey, historicalSuggestions, SuggestCacheTtl);
            return AiOperationResult<IReadOnlyList<CategorySuggestion>>.Ok(historicalSuggestions);
        }

        if (!_aiClient.IsConfigured)
        {
            return AiOperationResult<IReadOnlyList<CategorySuggestion>>.Failed("AI category suggestions are not configured.");
        }

        var categoriesJson = JsonSerializer.Serialize(categoryNames);
        var content = $@"Transaction description: {JsonSerializer.Serialize(description)}
Transaction type: {safeTxType}
Available categories JSON array: {categoriesJson}";

        string text;
        try
        {
            text = await _aiClient.GenerateTextAsync(
                [AiPart.FromText(content)],
                new AiGenerationOptions(
                    Feature: "category-suggestion",
                    Temperature: 0,
                    // Reasoning tokens count against MaxOutputTokens, so a tight budget
                    // lets the thinking pass starve the JSON output and trip MAX_TOKENS -- which the
                    // client treats as a hard failure, silently yielding zero suggestions. Keep
                    // thinking at its minimum ("low") and leave ample room for both it and the
                    // small JSON payload. (Omitting explicit reasoning effort is worse: the model
                    // then defaults to a larger, dynamic thinking budget.)
                    MaxOutputTokens: 512,
                    SystemInstruction: SuggestSystemInstruction,
                    OutputJsonSchema: AiResponseSchemas.CategorySuggestions(categoryNames),
                    ThinkingLevel: "low",
                    ModelConfigurationKey: "OpenAiModels:CategorySuggestion"),
                cancellationToken);
        }
        catch (AiClientException ex)
        {
            return AiOperationResult<IReadOnlyList<CategorySuggestion>>.Failed(ex.Message);
        }

        var suggestions = ParseSuggestions(text, categoryNames);
        _cache.Set(cacheKey, suggestions, SuggestCacheTtl);
        return AiOperationResult<IReadOnlyList<CategorySuggestion>>.Ok(suggestions);
    }

    private const string SuggestSystemInstruction = @"Rank the best transaction categories for a personal finance app.
Rules:
- category must be copied exactly from the available categories list.
- confidence is a number from 0.0 to 1.0.
- Return at most 3 suggestions, ordered from highest confidence to lowest.
- Do not include ledger categories. Do not create new category names.";

    public async Task<AiOperationResult<IReadOnlyList<TransactionNoteSuggestion>>> SuggestNotesAsync(
        string description,
        string? category,
        string? ledgerCategory,
        string? txType,
        IEnumerable<string>? historyDescriptions,
        CancellationToken cancellationToken = default)
    {
        if (!_aiClient.IsConfigured)
        {
            return AiOperationResult<IReadOnlyList<TransactionNoteSuggestion>>.Failed("AI category suggestions are not configured.");
        }

        var history = (historyDescriptions ?? [])
            .Select(h => h.Trim())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(h => h.Length > 150 ? h[..150] : h)
            .Take(8)
            .ToList();

        var content = $@"Current description: {JsonSerializer.Serialize(description)}
Transaction type: {JsonSerializer.Serialize(txType ?? "")}
Category: {JsonSerializer.Serialize(category ?? "")}
Ledger category: {JsonSerializer.Serialize(ledgerCategory ?? "")}
Recent description examples JSON array: {JsonSerializer.Serialize(history)}";

        string text;
        try
        {
            text = await _aiClient.GenerateTextAsync(
                [AiPart.FromText(content)],
                new AiGenerationOptions(
                    Feature: "note-suggestion",
                    Temperature: 0.2,
                    // Same MAX_TOKENS starvation risk as category-suggestion, and notes emit more
                    // text (3 notes + reasons), so give even more headroom on top of "low" thinking.
                    MaxOutputTokens: 640,
                    SystemInstruction: SuggestNotesSystemInstruction,
                    OutputJsonSchema: AiResponseSchemas.NoteSuggestions,
                    ThinkingLevel: "low",
                    ModelConfigurationKey: "OpenAiModels:NoteSuggestion"),
                cancellationToken);
        }
        catch (AiClientException ex)
        {
            return AiOperationResult<IReadOnlyList<TransactionNoteSuggestion>>.Failed(ex.Message);
        }

        return AiOperationResult<IReadOnlyList<TransactionNoteSuggestion>>.Ok(ParseNoteSuggestions(text));
    }

    private const string SuggestNotesSystemInstruction = @"Return up to 3 concise, useful transaction-description alternatives.
Rules:
- Do not invent details like people, locations, receipt numbers, or dates.
- Preserve merchant/product words if present.
- Keep each note under 70 characters.
- Use title case only when it looks natural for a merchant or proper name.
- Offer as many distinct alternatives as the input reasonably allows (e.g. a cleaned version and a shorter version). It is fine to return only 1 or 2 when the description is already short and clean -- do not pad with near-duplicates just to reach 3.";

    public async Task<AiOperationResult<CategoryCleanupReview>> ReviewCategoryCleanupAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _categoryService.GetCategoriesAsync();
        var visibleCategories = categories
            .Where(c => !TransactionCategoryService.IsReservedName(c.Name))
            .Select(c => c.Name.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        if (visibleCategories.Count == 0)
        {
            return AiOperationResult<CategoryCleanupReview>.Ok(new CategoryCleanupReview([]));
        }

        var recentTransactions = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.LedgerCategory != "Discarded")
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Take(1000)
            .Select(t => new CategoryReviewTransaction(t.Id, t.Description, t.Category, t.LedgerCategory))
            .ToListAsync(cancellationToken);

        var usage = visibleCategories
            .Select(category =>
            {
                var matches = recentTransactions
                    .Where(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase))
                    .Take(8)
                    .ToList();
                return new
                {
                    category,
                    count = recentTransactions.Count(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase)),
                    examples = matches.Select(t => t.Description).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList()
                };
            })
            .ToList();

        var content = $@"Existing categories JSON array: {JsonSerializer.Serialize(visibleCategories)}
Recent usage JSON array: {JsonSerializer.Serialize(usage)}";

        var cacheHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var cleanupCacheKey = CleanupCachePrefix + _context.RequireCurrentUserId() + ":" + cacheHash;
        if (_cache.TryGetValue(cleanupCacheKey, out CategoryCleanupReview? cachedReview) && cachedReview != null)
        {
            Telemetry.CacheHitsCounter.Add(1, new KeyValuePair<string, object?>("cache_type", "ai-category-cleanup"));
            return AiOperationResult<CategoryCleanupReview>.Ok(cachedReview);
        }
        Telemetry.CacheMissesCounter.Add(1, new KeyValuePair<string, object?>("cache_type", "ai-category-cleanup"));

        var deterministicSuggestions = usage
            .Where(item => item.count == 0)
            .Select(item => new CategoryCleanupSuggestion(
                $"cleanup-unused-{item.category.ToLowerInvariant()}",
                "delete",
                $"Remove {item.category}",
                "This category has no recent transactions.",
                [item.category],
                null,
                null,
                0,
                1))
            .Concat(usage
                .Where(item => item.count is >= 1 and <= 3)
                .Select(item => new CategoryCleanupSuggestion(
                    $"cleanup-low-use-{item.category.ToLowerInvariant()}",
                    "consolidate",
                    $"Consolidate {item.category}",
                    $"This category has only {item.count} recent transaction{(item.count == 1 ? "" : "s")}.",
                    [item.category],
                    null,
                    null,
                    item.count,
                    0.95)))
            .Take(5)
            .ToList();

        if (deterministicSuggestions.Count == 5)
        {
            var deterministicReview = new CategoryCleanupReview(deterministicSuggestions);
            _cache.Set(cleanupCacheKey, deterministicReview, CleanupCacheTtl);
            return AiOperationResult<CategoryCleanupReview>.Ok(deterministicReview);
        }

        if (!_aiClient.IsConfigured)
        {
            var deterministicReview = new CategoryCleanupReview(deterministicSuggestions);
            _cache.Set(cleanupCacheKey, deterministicReview, CleanupCacheTtl);
            return AiOperationResult<CategoryCleanupReview>.Ok(deterministicReview);
        }

        string text;
        try
        {
            text = await _aiClient.GenerateTextAsync(
                [AiPart.FromText(content)],
                new AiGenerationOptions(
                    Feature: "category-cleanup",
                    Temperature: 0.1,
                    MaxOutputTokens: 700,
                    SystemInstruction: ReviewCleanupSystemInstruction,
                    OutputJsonSchema: AiResponseSchemas.CategoryCleanup,
                    ThinkingLevel: "low",
                    ModelConfigurationKey: "OpenAiModels:CategoryCleanup"),
                cancellationToken);
        }
        catch (AiClientException ex)
        {
            return AiOperationResult<CategoryCleanupReview>.Failed(ex.Message);
        }

        var aiReview = ParseCategoryCleanupReview(text, visibleCategories, recentTransactions);
        var combined = deterministicSuggestions
            .Concat(aiReview.Suggestions)
            .GroupBy(suggestion => $"{suggestion.Type}:{string.Join('|', suggestion.Categories)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(5)
            .ToList();
        var review = new CategoryCleanupReview(combined);
        _cache.Set(cleanupCacheKey, review, CleanupCacheTtl);
        return AiOperationResult<CategoryCleanupReview>.Ok(review);
    }

    private const string ReviewCleanupSystemInstruction = @"Review custom transaction categories and suggest safe CATEGORY-level cleanup only.
Rules:
- Return at most 5 suggestions. An empty suggestions array is a completely valid and often correct answer when the existing categories already look healthy -- never invent a suggestion just to have something to say.
- Never suggest moving, recategorizing, or swapping an individual transaction. Every suggestion must act on a whole category, not a single entry -- that kind of change is out of scope here.
- For delete, only choose existing categories with zero recent transactions.
- For merge, only propose it when the two categories are genuinely duplicative in overall purpose across most of their usage (e.g. two categories that mean the same thing, like ""Streaming"" and ""Subscriptions""). Do not propose a merge just because one transaction in a category could also fit under another category -- a single overlapping entry is never sufficient reason to fold an entire category into another one.
- For consolidate, use this instead of merge when a category has very few recent transactions (roughly 1-3) so it's a candidate for cleanup, but you are not confident every transaction in it belongs in one specific other category. Always leave targetCategory null for consolidate -- never guess a destination category; the app will ask the user to manually pick where those few transactions should go.
- For add, newCategoryName must not already exist and should be broadly useful.
- Never suggest Transfer or Adjustment.
- Prefer conservative cleanup. If unsure, return fewer suggestions or none at all.";

    public async Task<CategoryCleanupApplyOutcome> ApplyCategoryCleanupAsync(IReadOnlyList<CategoryCleanupAction> actions)
    {
        if (actions.Count == 0)
        {
            return CategoryCleanupApplyOutcome.Ok(new CategoryCleanupApplyResult(0, []));
        }

        var appliedCount = 0;
        var undoActions = new List<CategoryCleanupAction>();
        string? conflictMessage = null;
        var strategy = _context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            // CreateExecutionStrategy retries this whole delegate on a transient DB failure,
            // so state mutated by a previous (failed) attempt must be reset here -- otherwise
            // a retry after a partial run would double-count appliedCount/undoActions.
            appliedCount = 0;
            undoActions.Clear();
            conflictMessage = null;

            await using var transaction = await _context.Database.BeginTransactionAsync();

            foreach (var action in actions.Take(20))
            {
                var step = await _cleanupApplier.ApplySingleCleanupActionAsync(action, undoActions);
                if (step.ConflictMessage != null)
                {
                    conflictMessage = step.ConflictMessage;
                    await transaction.RollbackAsync();
                    return;
                }

                appliedCount += step.AppliedCount;
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        });

        if (conflictMessage != null)
        {
            return CategoryCleanupApplyOutcome.Conflict(conflictMessage);
        }

        if (appliedCount > 0)
        {
            _categoryService.InvalidateCache();
        }

        return CategoryCleanupApplyOutcome.Ok(new CategoryCleanupApplyResult(appliedCount, undoActions));
    }

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
        IReadOnlyList<string> categoryNames,
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

        var canonicalByName = categoryNames.ToDictionary(c => c.ToLowerInvariant(), c => c);
        var existingLower = canonicalByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<CategoryCleanupSuggestion>();

        foreach (var item in suggestionsElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeProp)
                ? typeProp.GetString()?.Trim().ToLowerInvariant()
                : null;
            if (type is not ("delete" or "merge" or "add" or "consolidate")) continue;

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

            // The user picks the destination manually for a low-usage cleanup, so the AI is
            // never trusted to name a target here even if it tried to include one.
            if (type == "consolidate")
            {
                targetCategory = null;
            }

            if (type == "delete" && categories.Count == 0) continue;
            if (type == "merge" && (categories.Count == 0 || targetCategory == null)) continue;
            if (type == "consolidate" && categories.Count != 1) continue;
            if (type == "add" && (newCategoryName == null || existingLower.Contains(newCategoryName.ToLowerInvariant()))) continue;

            var affectedCount = type == "add"
                ? 0
                : recentTransactions.Count(t => categories.Contains((string)t.Category, StringComparer.OrdinalIgnoreCase));

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

            var title = item.TryGetProperty("title", out var titleProp)
                ? titleProp.GetString()?.Trim()
                : null;
            var summary = item.TryGetProperty("summary", out var summaryProp)
                ? summaryProp.GetString()?.Trim()
                : null;

            suggestions.Add(new CategoryCleanupSuggestion(
                $"cleanup-{suggestions.Count + 1}",
                type,
                string.IsNullOrWhiteSpace(title) ? DefaultCleanupTitle(type, categories, targetCategory, newCategoryName) : title!,
                string.IsNullOrWhiteSpace(summary) ? "AI found a category cleanup opportunity." : summary!,
                categories,
                targetCategory,
                newCategoryName,
                affectedCount,
                Math.Clamp(confidence, 0.0, 1.0)));
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
