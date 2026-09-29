using System.Globalization;
using System.Text.Json;

namespace FinancialAppApi.Services.AI.Tools;

// Typed, validated access to one call's arguments. Model-supplied arguments are untrusted: every
// accessor bounds its value and reports a correctable mistake as AiToolArgumentException.
// A JSON null is treated as absent, since the model often sends null for "not specified".
public sealed class AiToolArgs
{
    private readonly JsonElement _root;

    private AiToolArgs(JsonElement root) => _root = root;

    public static AiToolArgs Empty { get; } = Parse("{}");

    public static AiToolArgs Parse(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? new AiToolArgs(document.RootElement.Clone())
                : throw new AiToolArgumentException("Arguments must be a JSON object.");
        }
        catch (JsonException)
        {
            throw new AiToolArgumentException("Arguments were not valid JSON.");
        }
    }

    public string? OptionalString(string name, int maxLength = 200)
    {
        if (!TryGet(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid(name, "a string");
        var text = value.GetString()!.Trim();
        if (text.Length == 0) return null;
        return text.Length <= maxLength ? text : throw new AiToolArgumentException($"{name} must be at most {maxLength} characters.");
    }

    public string RequiredString(string name, int maxLength = 200) =>
        OptionalString(name, maxLength) ?? throw new AiToolArgumentException($"{name} is required.");

    public string? OptionalEnum(string name, IReadOnlyCollection<string> allowed)
    {
        var text = OptionalString(name, 64);
        if (text == null) return null;
        var match = allowed.FirstOrDefault(option => option.Equals(text, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new AiToolArgumentException($"{name} must be one of: {string.Join(", ", allowed)}.");
    }

    public int? OptionalInt(string name, int min, int max)
    {
        if (!TryGet(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw Invalid(name, "a whole number");
        return number >= min && number <= max
            ? number
            : throw new AiToolArgumentException($"{name} must be between {min} and {max}.");
    }

    public decimal? OptionalDecimal(string name, decimal min, decimal max)
    {
        if (!TryGet(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number)) throw Invalid(name, "a number");
        return number >= min && number <= max
            ? number
            : throw new AiToolArgumentException($"{name} must be between {min} and {max}.");
    }

    public DateOnly? OptionalDate(string name)
    {
        var text = OptionalString(name, 10);
        if (text == null) return null;
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new AiToolArgumentException($"{name} must be a date in yyyy-MM-dd form.");
    }

    public IReadOnlyList<string> OptionalStringArray(string name, int maxItems, int maxLength = 64)
    {
        if (!TryGet(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw Invalid(name, "an array of strings");
        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw Invalid(name, "an array of strings");
            var text = item.GetString()!.Trim();
            if (text.Length == 0) continue;
            if (text.Length > maxLength) throw new AiToolArgumentException($"Each {name} entry must be at most {maxLength} characters.");
            items.Add(text);
        }
        return items.Count <= maxItems
            ? items.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : throw new AiToolArgumentException($"{name} accepts at most {maxItems} entries.");
    }

    // Canonical text used to recognise a repeated identical call within one turn.
    public string CanonicalJson() => JsonSerializer.Serialize(_root);

    private bool TryGet(string name, out JsonElement value)
    {
        if (_root.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null) return true;
        value = default;
        return false;
    }

    private static AiToolArgumentException Invalid(string name, string expected) =>
        new($"{name} must be {expected}.");
}
