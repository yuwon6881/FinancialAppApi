using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FinancialAppApi.Services.Documents;

public sealed record VaultDocumentTypeDto(string Id, string Name, int UsageCount);

public sealed record VaultTypeCleanupSuggestion(
    string Id,
    string Type,
    string Title,
    string Summary,
    IReadOnlyList<string> Categories,
    string? TargetCategory,
    string? NewCategoryName,
    int AffectedTransactionCount,
    double Confidence);

public sealed record VaultTypeCleanupAction(
    string Type,
    IReadOnlyList<string>? Categories,
    string? TargetCategory,
    string? NewCategoryName);

public sealed class VaultDocumentTypeService
{
    public static readonly string[] DefaultNames =
    [
        "Receipt", "Invoice", "Tax Return", "Bank Statement",
        "Donation Certificate", "Medical Bill", "Insurance Policy", "Other"
    ];

    private static readonly TimeSpan ReviewCacheTtl = TimeSpan.FromMinutes(10);
    private readonly AppDbContext _context;
    private readonly AiClient _aiClient;
    private readonly IMemoryCache _cache;

    public VaultDocumentTypeService(AppDbContext context, AiClient aiClient, IMemoryCache cache)
    {
        _context = context;
        _aiClient = aiClient;
        _cache = cache;
    }

    public async Task<IReadOnlyList<VaultDocumentTypeDto>> ListAsync(CancellationToken ct = default)
    {
        var counts = await _context.VaultDocuments
            .AsNoTracking()
            .GroupBy(document => document.DocumentType)
            .Select(group => new { Name = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Name, item => item.Count, ct);

        var types = await _context.VaultDocumentTypes
            .AsNoTracking()
            .OrderBy(type => type.Name)
            .ToListAsync(ct);

        return types
            .Select(type => new VaultDocumentTypeDto(
                type.Id,
                type.Name,
                counts.GetValueOrDefault(type.Name)))
            .ToList();
    }

    public async Task<(VaultDocumentTypeDto? Type, string? Error)> CreateAsync(
        string? name,
        string? id,
        CancellationToken ct = default)
    {
        var cleaned = CleanName(name);
        if (cleaned == null) return (null, "Document type must be between 1 and 40 characters.");

        var exists = await _context.VaultDocumentTypes.AnyAsync(
            type => type.Name.ToLower() == cleaned.ToLower(), ct);
        if (exists) return (null, $"Document type '{cleaned}' already exists.");

        var type = new VaultDocumentTypeDefinition
        {
            Id = string.IsNullOrWhiteSpace(id) ? $"doctype-{Guid.NewGuid():N}" : id.Trim(),
            Name = cleaned
        };
        if (type.Id.Length > 64) return (null, "Document type id is too long.");

        _context.VaultDocumentTypes.Add(type);
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _context.Entry(type).State = EntityState.Detached;
            if (await _context.VaultDocumentTypes.AsNoTracking().AnyAsync(
                item => item.Name.ToLower() == cleaned.ToLower(), ct))
            {
                return (null, $"Document type '{cleaned}' already exists.");
            }
            throw;
        }
        return (new VaultDocumentTypeDto(type.Id, type.Name, 0), null);
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(
        string id,
        string? replacementId,
        CancellationToken ct = default)
    {
        var type = await _context.VaultDocumentTypes.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (type == null) return (false, "Document type was not found.");

        var matchingDocuments = await _context.VaultDocuments
            .Where(document => document.DocumentType == type.Name)
            .ToListAsync(ct);
        VaultDocumentTypeDefinition? replacement = null;
        if (matchingDocuments.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(replacementId))
                return (false, "Document type is in use. Choose a replacement before deleting it.");

            replacement = await _context.VaultDocumentTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == replacementId && item.Id != id, ct);
            if (replacement == null) return (false, "Choose an existing replacement document type.");

        }

        if (replacement != null)
        {
            foreach (var document in matchingDocuments)
            {
                document.DocumentType = replacement.Name;
            }
        }
        _context.VaultDocumentTypes.Remove(type);
        await _context.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken ct = default)
    {
        var cleaned = CleanName(name);
        return cleaned != null && await _context.VaultDocumentTypes
            .AsNoTracking()
            .AnyAsync(type => type.Name.ToLower() == cleaned.ToLower(), ct);
    }

    public async Task<IReadOnlyList<VaultTypeCleanupSuggestion>> ReviewAsync(CancellationToken ct = default)
    {
        var types = await ListAsync(ct);
        var content = JsonSerializer.Serialize(types.Select(type => new { type.Name, type.UsageCount }));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var cacheKey = $"vault-type-review:{_context.RequireCurrentUserId()}:{hash}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<VaultTypeCleanupSuggestion>? cached) && cached != null)
            return cached;

        var deterministic = types
            .Where(type => type.UsageCount == 0)
            .Take(3)
            .Select(type => new VaultTypeCleanupSuggestion(
                $"vault-unused-{type.Id}", "delete", $"Remove {type.Name}",
                "This document type is not used by any saved documents.",
                [type.Name], null, null, 0, 1))
            .ToList();

        if (!_aiClient.IsConfigured || deterministic.Count >= 5)
        {
            _cache.Set(cacheKey, deterministic, ReviewCacheTtl);
            return deterministic;
        }

        string response;
        try
        {
            response = await _aiClient.GenerateTextAsync(
                [AiPart.FromText($"Existing document types and usage counts JSON: {content}")],
                new AiGenerationOptions(
                    Feature: "vault-type-cleanup",
                    Temperature: 0.1,
                    MaxOutputTokens: 600,
                    SystemInstruction: ReviewInstruction,
                    OutputJsonSchema: AiResponseSchemas.CategoryCleanup,
                    ThinkingLevel: "low",
                    ModelConfigurationKey: "OpenAiModels:CategoryCleanup"),
                ct);
        }
        catch (AiClientException)
        {
            _cache.Set(cacheKey, deterministic, ReviewCacheTtl);
            return deterministic;
        }

        var ai = ParseReview(response, types);
        var combined = deterministic
            .Concat(ai)
            .GroupBy(item => $"{item.Type}:{string.Join('|', item.Categories)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(5)
            .ToList();
        _cache.Set(cacheKey, combined, ReviewCacheTtl);
        return combined;
    }

    public async Task<(bool Success, string? Error)> ApplyAsync(
        VaultTypeCleanupAction action,
        CancellationToken ct = default)
    {
        var names = (action.Categories ?? [])
            .Select(CleanName)
            .Where(name => name != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (action.Type == "add")
        {
            var created = await CreateAsync(action.NewCategoryName, null, ct);
            return (created.Type != null, created.Error);
        }

        if (names.Count == 0) return (false, "The cleanup action has no source document type.");
        var sources = await _context.VaultDocumentTypes
            .Where(type => names.Select(name => name.ToLower()).Contains(type.Name.ToLower()))
            .ToListAsync(ct);
        if (sources.Count != names.Count) return (false, "A source document type no longer exists.");

        if (action.Type == "delete")
        {
            foreach (var source in sources)
            {
                var deleted = await DeleteAsync(source.Id, null, ct);
                if (!deleted.Success) return deleted;
            }
            return (true, null);
        }
        if (action.Type is not ("merge" or "consolidate"))
            return (false, "Unsupported document type cleanup action.");

        var targetName = CleanName(action.TargetCategory);
        var target = targetName == null
            ? null
            : await _context.VaultDocumentTypes.FirstOrDefaultAsync(
                type => type.Name.ToLower() == targetName.ToLower() &&
                    !sources.Select(source => source.Id).Contains(type.Id), ct);
        if (target == null) return (false, "Choose an existing destination document type.");
        foreach (var source in sources)
        {
            var merged = await DeleteAsync(source.Id, target.Id, ct);
            if (!merged.Success) return merged;
        }
        return (true, null);
    }

    private static string? CleanName(string? name)
    {
        var cleaned = name?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) || cleaned.Length > 40 ? null : cleaned;
    }

    private static IReadOnlyList<VaultTypeCleanupSuggestion> ParseReview(
        string text,
        IReadOnlyList<VaultDocumentTypeDto> types)
    {
        try
        {
            using var json = JsonDocument.Parse(text.Trim().Trim('`'));
            var root = json.RootElement;
            var items = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("suggestions", out var suggestions) ? suggestions : default;
            if (items.ValueKind != JsonValueKind.Array) return [];

            var byName = types.ToDictionary(type => type.Name, StringComparer.OrdinalIgnoreCase);
            var result = new List<VaultTypeCleanupSuggestion>();
            foreach (var item in items.EnumerateArray())
            {
                var kind = item.TryGetProperty("type", out var kindElement) ? kindElement.GetString() : null;
                var categories = item.TryGetProperty("categories", out var categoriesElement)
                    ? categoriesElement.EnumerateArray()
                        .Select(value => value.GetString())
                        .Where(value => value != null && byName.ContainsKey(value))
                        .Cast<string>()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : [];
                if (kind is not ("delete" or "merge" or "consolidate" or "add")) continue;
                if (kind != "add" && categories.Count == 0) continue;
                if (kind == "delete" && categories.Any(name => byName[name].UsageCount > 0)) continue;

                var target = item.TryGetProperty("targetCategory", out var targetElement) &&
                    targetElement.ValueKind == JsonValueKind.String ? targetElement.GetString() : null;
                if (target != null && !byName.ContainsKey(target)) target = null;
                var newName = item.TryGetProperty("newCategoryName", out var newElement) &&
                    newElement.ValueKind == JsonValueKind.String ? CleanName(newElement.GetString()) : null;
                var confidence = item.TryGetProperty("confidence", out var confidenceElement) &&
                    confidenceElement.TryGetDouble(out var parsedConfidence) ? Math.Clamp(parsedConfidence, 0, 1) : 0.5;
                var affected = categories.Sum(name => byName[name].UsageCount);

                result.Add(new VaultTypeCleanupSuggestion(
                    $"vault-ai-{result.Count + 1}-{hash(kind, categories)}",
                    kind,
                    item.TryGetProperty("title", out var title) ? title.GetString() ?? "Document type cleanup" : "Document type cleanup",
                    item.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "Review this document type change." : "Review this document type change.",
                    categories,
                    target,
                    newName,
                    affected,
                    confidence));
            }
            return result.Take(5).ToList();
        }
        catch (JsonException)
        {
            return [];
        }

        static string hash(string kind, IReadOnlyList<string> names) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{string.Join('|', names)}")))[..8];
    }

    private const string ReviewInstruction = """
        Review document-vault type names and usage counts. Suggest only safe type-level cleanup.
        Return at most 5 suggestions and return an empty array when the list is already healthy.
        Delete only types with zero usage. Merge only genuinely duplicate meanings.
        Use consolidate for a low-use type when the user should choose the destination.
        Do not move individual documents. Prefer conservative suggestions.
        """;
}
