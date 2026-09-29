namespace FinancialAppApi.Services.AI.Tools;

// The catalogue of tools offered to the model. Definitions are emitted in a fixed name order so
// the tools block is byte-identical on every request and stays inside the cached prompt prefix.
public sealed class AiToolRegistry
{
    private readonly IReadOnlyDictionary<string, IAiTool> _tools;

    public AiToolRegistry(IEnumerable<IAiTool> tools)
    {
        var ordered = tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();
        var duplicate = ordered
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"AI tool name '{duplicate.Key}' is registered more than once.");

        _tools = ordered.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        Tools = ordered;
        Definitions = ordered
            .Select(tool => new AiFunctionTool(tool.Name, tool.Description, tool.ParametersSchema))
            .ToList();
    }

    public IReadOnlyList<IAiTool> Tools { get; }
    public IReadOnlyList<AiFunctionTool> Definitions { get; }

    public IAiTool? Find(string name) => _tools.GetValueOrDefault(name);
}
