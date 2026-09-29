namespace FinancialAppApi.Services.AI.Tools;

// Per-turn state shared by every tool call in one assistant turn.
public sealed class AiToolContext
{
    private readonly Func<CancellationToken, Task<bool>> _readSensitiveSetting;

    public AiToolContext(
        bool sensitiveMode,
        int cycleDay,
        string currency,
        DateOnly today,
        Func<CancellationToken, Task<bool>> readSensitiveSetting,
        AiTurnBudget? budget = null)
    {
        SensitiveMode = sensitiveMode;
        CycleDay = cycleDay;
        Currency = currency;
        Today = today;
        _readSensitiveSetting = readSensitiveSetting;
        Budget = budget ?? new AiTurnBudget();
    }

    public bool SensitiveMode { get; private set; }
    public int CycleDay { get; }
    public string Currency { get; }
    public DateOnly Today { get; }
    public AiEvidenceLedger Evidence { get; } = new();
    public AiTurnBudget Budget { get; }

    // Sensitive mode may switch on while a turn is running, never off: a stale turn must not
    // reveal what the user has just hidden, but a turn that started hidden stays hidden.
    public async Task RefreshSensitiveModeAsync(CancellationToken cancellationToken)
    {
        if (SensitiveMode) return;
        SensitiveMode = await _readSensitiveSetting(cancellationToken);
    }
}

// Every record id a tool showed the model this turn. A proposed action may only target a
// record the model actually saw, so an id cannot be guessed or lifted from untrusted text.
public sealed class AiEvidenceLedger
{
    public const string Transaction = "transaction";
    public const string Recurring = "recurring";
    public const string Wishlist = "wishlist";
    public const string SavingsGoal = "savingsGoal";
    public const string Loan = "loan";
    public const string Account = "account";
    public const string Instrument = "instrument";

    private readonly HashSet<(string Kind, string Id)> _records = [];

    public void Record(string kind, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)) _records.Add((kind, id));
    }

    public void RecordAll(string kind, IEnumerable<string?> ids)
    {
        foreach (var id in ids) Record(kind, id);
    }

    public bool Contains(string kind, string? id) =>
        !string.IsNullOrWhiteSpace(id) && _records.Contains((kind, id));

    public IReadOnlyList<string> IdsOf(string kind) =>
        _records.Where(record => record.Kind == kind).Select(record => record.Id).ToList();
}

// Bounds one turn's tool work so a looping model cannot run up cost or flood its own context.
public sealed class AiTurnBudget
{
    public const int DefaultMaxToolCalls = 8;
    public const int DefaultMaxResultCharacters = 40_000;
    public const int DefaultMaxCharactersPerResult = 12_000;

    public AiTurnBudget(
        int maxToolCalls = DefaultMaxToolCalls,
        int maxResultCharacters = DefaultMaxResultCharacters,
        int maxCharactersPerResult = DefaultMaxCharactersPerResult)
    {
        RemainingToolCalls = maxToolCalls;
        RemainingResultCharacters = maxResultCharacters;
        MaxCharactersPerResult = maxCharactersPerResult;
    }

    public int RemainingToolCalls { get; private set; }
    public int RemainingResultCharacters { get; private set; }
    public int MaxCharactersPerResult { get; }

    public bool TryReserveCall()
    {
        if (RemainingToolCalls <= 0) return false;
        RemainingToolCalls--;
        return true;
    }

    public bool TryConsumeCharacters(int count)
    {
        if (count > MaxCharactersPerResult || count > RemainingResultCharacters) return false;
        RemainingResultCharacters -= count;
        return true;
    }
}
