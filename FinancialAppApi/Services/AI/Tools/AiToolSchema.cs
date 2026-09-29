using System.Text.Json.Nodes;

namespace FinancialAppApi.Services.AI.Tools;

// Compact builders for tool parameter schemas. Every parameter is optional unless listed in
// required: the model omits what the user did not say, and the tool picks a widening default.
public static class AiToolSchema
{
    public static JsonObject Object(IEnumerable<(string Name, JsonObject Schema)> properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties) props[name] = schema;
        var schemaObject = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false
        };
        if (required.Length > 0) schemaObject["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return schemaObject;
    }

    public static JsonObject String(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description
    };

    public static JsonObject Enum(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray())
    };

    public static JsonObject Integer(string description, int minimum, int maximum) => new()
    {
        ["type"] = "integer",
        ["description"] = description,
        ["minimum"] = minimum,
        ["maximum"] = maximum
    };

    public static JsonObject Number(string description, decimal minimum) => new()
    {
        ["type"] = "number",
        ["description"] = description,
        ["minimum"] = minimum
    };

    public static JsonObject Date(string description) => new()
    {
        ["type"] = "string",
        ["description"] = $"{description} Format yyyy-MM-dd."
    };

    public static JsonObject StringArray(string description, int maxItems) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = "string" },
        ["maxItems"] = maxItems
    };
}
