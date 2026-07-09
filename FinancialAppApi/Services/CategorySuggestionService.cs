using System.Net.Http.Headers;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

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

public sealed class CategorySuggestionService
{
    private sealed record CategoryReviewTransaction(string Id, string Description, string Category, string LedgerCategory, decimal Amount);

    private readonly HttpClient _httpClient;
    private readonly AppDbContext _context;
    private readonly TransactionCategoryService _categoryService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CategorySuggestionService> _logger;

    public CategorySuggestionService(
        HttpClient httpClient,
        AppDbContext context,
        TransactionCategoryService categoryService,
        IConfiguration configuration,
        ILogger<CategorySuggestionService> logger)
    {
        _httpClient = httpClient;
        _context = context;
        _categoryService = categoryService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CategorySuggestion>> SuggestAsync(
        string description,
        string? txType,
        IEnumerable<string>? requestedCategories)
    {
        var categoryNames = NormalizeCategoryNames(requestedCategories).ToList();
        if (categoryNames.Count == 0)
        {
            categoryNames = (await _categoryService.GetCategoriesAsync())
                .Select(c => c.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name)
                .ToList();
        }

        if (categoryNames.Count == 0)
        {
            return [];
        }

        var apiKey = _configuration["GeminiApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new CategorySuggestionUserException("AI category suggestions are not configured.");
        }

        var safeTxType = string.Equals(txType, "inflow", StringComparison.OrdinalIgnoreCase)
            ? "inflow"
            : "outflow";
        var categoriesJson = JsonSerializer.Serialize(categoryNames);
        var prompt = $@"You suggest transaction categories for a personal finance app.
Score every available category internally for how well it matches the transaction description, then return only the top 3.
Return ONLY valid JSON, no markdown, no explanation.

Transaction description: {JsonSerializer.Serialize(description)}
Transaction type: {safeTxType}
Available categories JSON array: {categoriesJson}

JSON schema:
{{
  ""suggestions"": [
    {{ ""category"": ""<exact category name from the available list>"", ""confidence"": <0.0 to 1.0> }}
  ]
}}

Rules:
- category must be copied exactly from the available categories list.
- confidence is a number from 0.0 to 1.0.
- Return at most 3 suggestions, ordered from highest confidence to lowest.
- Do not include ledger categories. Do not create new category names.";

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.0,
                maxOutputTokens = 512
            }
        };

        var primaryModel = _configuration["GeminiModel"];
        if (string.IsNullOrWhiteSpace(primaryModel))
        {
            primaryModel = "gemini-3.1-flash-lite";
        }
        const string fallbackModel = "gemini-3.5-flash";

        string text;
        try
        {
            text = await CallGeminiAsync(primaryModel, requestBody, apiKey);
        }
        catch (GeminiUnavailableException)
        {
            _logger.LogWarning("Primary model {PrimaryModel} unavailable, retrying with fallback {FallbackModel}.", primaryModel, fallbackModel);
            text = await CallGeminiAsync(fallbackModel, requestBody, apiKey);
        }

        return ParseSuggestions(text, categoryNames);
    }

    public async Task<IReadOnlyList<TransactionNoteSuggestion>> SuggestNotesAsync(
        string description,
        string? category,
        string? ledgerCategory,
        string? txType,
        IEnumerable<string>? historyDescriptions)
    {
        var apiKey = GetApiKey();
        var history = (historyDescriptions ?? [])
            .Select(h => h.Trim())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        var prompt = $@"You rewrite transaction descriptions for a personal finance ledger.
Return exactly 3 concise, useful alternatives for the user to select.
Return ONLY valid JSON, no markdown, no explanation.

Current description: {JsonSerializer.Serialize(description)}
Transaction type: {JsonSerializer.Serialize(txType ?? "")}
Category: {JsonSerializer.Serialize(category ?? "")}
Ledger category: {JsonSerializer.Serialize(ledgerCategory ?? "")}
Recent description examples JSON array: {JsonSerializer.Serialize(history)}

JSON schema:
{{
  ""notes"": [
    {{ ""note"": ""<clean transaction description>"", ""reason"": ""<short reason>"" }}
  ]
}}

Rules:
- Do not invent details like people, locations, receipt numbers, or dates.
- Preserve merchant/product words if present.
- Keep each note under 70 characters.
- Use title case only when it looks natural for a merchant or proper name.
- The three notes should be meaningfully different: cleaned, shorter, and more specific if possible.";

        var text = await GenerateGeminiTextAsync(prompt, 0.25, 512);
        return ParseNoteSuggestions(text);
    }

    public async Task<CategoryCleanupReview> ReviewCategoryCleanupAsync()
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
            return new CategoryCleanupReview([]);
        }

        var recentTransactions = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.LedgerCategory != "Discarded")
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .Take(1000)
            .Select(t => new CategoryReviewTransaction(t.Id, t.Description, t.Category, t.LedgerCategory, t.Amount))
            .ToListAsync();

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

        var prompt = $@"You review custom transaction categories for a personal finance app.
Suggest safe cleanup actions at the CATEGORY level only. Return ONLY valid JSON, no markdown, no explanation.

Existing categories JSON array: {JsonSerializer.Serialize(visibleCategories)}
Recent usage JSON array: {JsonSerializer.Serialize(usage)}

JSON schema:
{{
  ""suggestions"": [
    {{
      ""type"": ""delete"" | ""merge"" | ""add"" | ""consolidate"",
      ""title"": ""<short title>"",
      ""summary"": ""<one-sentence reason>"",
      ""categories"": [""<existing category names involved>""],
      ""targetCategory"": ""<existing category name for merge, or null>"",
      ""newCategoryName"": ""<new category name for add, or null>"",
      ""confidence"": <0.0 to 1.0>
    }}
  ]
}}

Rules:
- Return at most 5 suggestions. An empty suggestions array is a completely valid and often correct answer when the existing categories already look healthy -- never invent a suggestion just to have something to say.
- Never suggest moving, recategorizing, or swapping an individual transaction. Every suggestion must act on a whole category, not a single entry -- that kind of change is out of scope here.
- For delete, only choose existing categories with zero recent transactions.
- For merge, only propose it when the two categories are genuinely duplicative in overall purpose across most of their usage (e.g. two categories that mean the same thing, like ""Subscriptions"" and ""Software""). Do not propose a merge just because one transaction in a category could also fit under another category -- a single overlapping entry is never sufficient reason to fold an entire category into another one.
- For consolidate, use this instead of merge when a category has very few recent transactions (roughly 1-3) so it's a candidate for cleanup, but you are not confident every transaction in it belongs in one specific other category. Always leave targetCategory null for consolidate -- never guess a destination category; the app will ask the user to manually pick where those few transactions should go.
- For add, newCategoryName must not already exist and should be broadly useful.
- Never suggest Transfer or Adjustment.
- Prefer conservative cleanup. If unsure, return fewer suggestions or none at all.";

        var text = await GenerateGeminiTextAsync(prompt, 0.1, 1024);
        return ParseCategoryCleanupReview(text, visibleCategories, recentTransactions);
    }

    public async Task<CategoryCleanupApplyResult> ApplyCategoryCleanupAsync(IReadOnlyList<CategoryCleanupAction> actions)
    {
        if (actions.Count == 0)
        {
            return new CategoryCleanupApplyResult(0, []);
        }

        var appliedCount = 0;
        var undoActions = new List<CategoryCleanupAction>();
        var strategy = _context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();

            foreach (var action in actions.Take(20))
            {
                appliedCount += await ApplySingleCleanupActionAsync(action, undoActions);
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        });

        if (appliedCount > 0)
        {
            _categoryService.InvalidateCache();
        }

        return new CategoryCleanupApplyResult(appliedCount, undoActions);
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

    private async Task<int> ApplySingleCleanupActionAsync(CategoryCleanupAction action, List<CategoryCleanupAction> undoActions)
    {
        return action.Type.Trim().ToLowerInvariant() switch
        {
            "add" => await ApplyAddCategoryAsync(action, undoActions),
            "delete" => await ApplyDeleteCategoryAsync(action, undoActions),
            "merge" => await ApplyMergeCategoryAsync(action, undoActions),
            "deletebyname" => await ApplyDeleteByNameAsync(action),
            "restoretransactions" => await ApplyRestoreTransactionsAsync(action),
            "restorerecurringpayments" => await ApplyRestoreRecurringPaymentsAsync(action),
            _ => 0
        };
    }

    private async Task<int> ApplyAddCategoryAsync(CategoryCleanupAction action, List<CategoryCleanupAction> undoActions)
    {
        var name = CleanCategoryName(action.NewCategoryName);
        if (name == null || TransactionCategoryService.IsReservedName(name))
        {
            return 0;
        }

        var exists = await _context.TransactionCategories.AnyAsync(c => c.Name.ToLower() == name.ToLower());
        if (exists)
        {
            return 0;
        }

        var categoryId = string.IsNullOrWhiteSpace(action.CategoryId) ? $"cat-{Guid.NewGuid():N}" : action.CategoryId.Trim();
        _context.TransactionCategories.Add(new TransactionCategory { Id = categoryId, Name = name });
        undoActions.Insert(0, new CategoryCleanupAction("deleteByName", Categories: [name]));
        return 1;
    }

    private async Task<int> ApplyDeleteCategoryAsync(CategoryCleanupAction action, List<CategoryCleanupAction> undoActions)
    {
        var applied = 0;
        foreach (var name in NormalizeActionCategories(action.Categories))
        {
            var category = await _context.TransactionCategories.FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower());
            if (category == null || TransactionCategoryService.IsReservedName(category.Name))
            {
                continue;
            }

            var inUse = await _context.Transactions.AnyAsync(t => t.Category.ToLower() == category.Name.ToLower()) ||
                await _context.RecurringPayments.AnyAsync(rp => rp.Category.ToLower() == category.Name.ToLower());
            if (inUse)
            {
                throw new CategorySuggestionUserException("Category is in use. Choose a replacement category before deleting it.");
            }

            _context.TransactionCategories.Remove(category);
            undoActions.Insert(0, new CategoryCleanupAction("add", NewCategoryName: category.Name, CategoryId: category.Id));
            applied++;
        }

        return applied;
    }

    private async Task<int> ApplyMergeCategoryAsync(CategoryCleanupAction action, List<CategoryCleanupAction> undoActions)
    {
        var targetName = CleanCategoryName(action.TargetCategory);
        if (targetName == null || TransactionCategoryService.IsReservedName(targetName))
        {
            return 0;
        }

        var target = await _context.TransactionCategories.FirstOrDefaultAsync(c => c.Name.ToLower() == targetName.ToLower());
        if (target == null)
        {
            throw new CategorySuggestionUserException("Replacement category no longer exists.");
        }

        var applied = 0;
        foreach (var sourceName in NormalizeActionCategories(action.Categories))
        {
            if (string.Equals(sourceName, target.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = await _context.TransactionCategories.FirstOrDefaultAsync(c => c.Name.ToLower() == sourceName.ToLower());
            if (source == null || TransactionCategoryService.IsReservedName(source.Name))
            {
                continue;
            }

            var transactionIds = await _context.Transactions
                .Where(t => t.Category.ToLower() == source.Name.ToLower())
                .Select(t => t.Id)
                .ToListAsync();
            var recurringPaymentIds = await _context.RecurringPayments
                .Where(rp => rp.Category.ToLower() == source.Name.ToLower())
                .Select(rp => rp.Id)
                .ToListAsync();

            foreach (var transaction in await _context.Transactions.Where(t => transactionIds.Contains(t.Id)).ToListAsync())
            {
                transaction.Category = target.Name;
            }

            foreach (var recurringPayment in await _context.RecurringPayments.Where(rp => recurringPaymentIds.Contains(rp.Id)).ToListAsync())
            {
                recurringPayment.Category = target.Name;
            }

            _context.TransactionCategories.Remove(source);
            undoActions.Insert(0, new CategoryCleanupAction("restoreRecurringPayments", TargetCategory: source.Name, RecurringPaymentIds: recurringPaymentIds));
            undoActions.Insert(0, new CategoryCleanupAction("restoreTransactions", TargetCategory: source.Name, TransactionIds: transactionIds));
            undoActions.Insert(0, new CategoryCleanupAction("add", NewCategoryName: source.Name, CategoryId: source.Id));
            applied++;
        }

        return applied;
    }

    private async Task<int> ApplyDeleteByNameAsync(CategoryCleanupAction action)
    {
        var applied = 0;
        foreach (var name in NormalizeActionCategories(action.Categories))
        {
            var category = await _context.TransactionCategories.FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower());
            if (category == null || TransactionCategoryService.IsReservedName(category.Name))
            {
                continue;
            }

            var inUse = await _context.Transactions.AnyAsync(t => t.Category.ToLower() == category.Name.ToLower()) ||
                await _context.RecurringPayments.AnyAsync(rp => rp.Category.ToLower() == category.Name.ToLower());
            if (inUse)
            {
                throw new CategorySuggestionUserException("Undo could not remove a category that is now in use.");
            }

            _context.TransactionCategories.Remove(category);
            applied++;
        }

        return applied;
    }

    private async Task<int> ApplyRestoreTransactionsAsync(CategoryCleanupAction action)
    {
        var target = CleanCategoryName(action.TargetCategory);
        var ids = NormalizeIds(action.TransactionIds);
        if (target == null || ids.Count == 0)
        {
            return 0;
        }

        var transactions = await _context.Transactions.Where(t => ids.Contains(t.Id)).ToListAsync();
        foreach (var transaction in transactions)
        {
            transaction.Category = target;
        }

        return transactions.Count;
    }

    private async Task<int> ApplyRestoreRecurringPaymentsAsync(CategoryCleanupAction action)
    {
        var target = CleanCategoryName(action.TargetCategory);
        var ids = NormalizeIds(action.RecurringPaymentIds);
        if (target == null || ids.Count == 0)
        {
            return 0;
        }

        var recurringPayments = await _context.RecurringPayments.Where(rp => ids.Contains(rp.Id)).ToListAsync();
        foreach (var recurringPayment in recurringPayments)
        {
            recurringPayment.Category = target;
        }

        return recurringPayments.Count;
    }

    private async Task<string> GenerateGeminiTextAsync(string prompt, double temperature, int maxOutputTokens)
    {
        var apiKey = GetApiKey();
        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature,
                maxOutputTokens
            }
        };

        var primaryModel = _configuration["GeminiModel"];
        if (string.IsNullOrWhiteSpace(primaryModel))
        {
            primaryModel = "gemini-3.1-flash-lite";
        }
        const string fallbackModel = "gemini-3.5-flash";

        try
        {
            return await CallGeminiAsync(primaryModel, requestBody, apiKey);
        }
        catch (GeminiUnavailableException)
        {
            _logger.LogWarning("Primary model {PrimaryModel} unavailable, retrying with fallback {FallbackModel}.", primaryModel, fallbackModel);
            return await CallGeminiAsync(fallbackModel, requestBody, apiKey);
        }
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

    private async Task<string> CallGeminiAsync(string model, object requestBody, string apiKey)
    {
        var geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, geminiUrl);
        httpRequest.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(requestBody));
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await _httpClient.SendAsync(httpRequest);

        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            var unavailableBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Gemini API 503 (model {Model}): {Body}", model, unavailableBody);
            throw new GeminiUnavailableException();
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var quotaBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Gemini API 429 (model {Model}): {Body}", model, quotaBody);
            throw new CategorySuggestionUserException("AI service rate limit reached. Please wait a moment and try again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Gemini API error {Status} (model {Model}): {Body}", response.StatusCode, model, errorBody);
            throw new CategorySuggestionUserException("AI service returned an error. Please try again.");
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        using var geminiDoc = JsonDocument.Parse(responseBody);
        var candidate = geminiDoc.RootElement.GetProperty("candidates")[0];
        var text = candidate
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";

        if (candidate.TryGetProperty("finishReason", out var finishReasonProp) &&
            finishReasonProp.GetString() == "MAX_TOKENS")
        {
            _logger.LogWarning("Gemini response truncated by MAX_TOKENS (model {Model}). Partial text: {RawText}", model, text);
            throw new CategorySuggestionUserException("AI response was too long and got cut off. Please try again.");
        }

        return text;
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

    private string GetApiKey()
    {
        var apiKey = _configuration["GeminiApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new CategorySuggestionUserException("AI category suggestions are not configured.");
        }

        return apiKey;
    }

    private static string? CleanCategoryName(string? name)
    {
        var cleaned = name?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return null;
        return cleaned.Length > 100 ? cleaned[..100].Trim() : cleaned;
    }

    private static IReadOnlyList<string> NormalizeActionCategories(IEnumerable<string>? categories)
    {
        return (categories ?? [])
            .Select(CleanCategoryName)
            .Where(name => name != null && !TransactionCategoryService.IsReservedName(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    private static IReadOnlyList<string> NormalizeIds(IEnumerable<string>? ids)
    {
        return (ids ?? [])
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(1000)
            .ToList();
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

    private sealed class GeminiUnavailableException : Exception { }
}

public sealed class CategorySuggestionUserException : Exception
{
    public CategorySuggestionUserException(string message) : base(message)
    {
    }
}
