using System.Text.Json.Nodes;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Services.AI.Tools;

// Account and bucket balances. Balances come from LedgerAccountService, which resumes from the
// CycleBalance snapshot; they are never re-derived here by summing transactions.
public sealed class GetAccountsTool : IAiTool
{
    private static readonly string[] Buckets = ["Essentials", "Growth", "Stability", "Rewards"];

    private readonly LedgerAccountService _accounts;

    public GetAccountsTool(LedgerAccountService accounts) => _accounts = accounts;

    public string Name => "get_accounts";

    public string Description =>
        "List the user's ledger accounts (bank, e-wallet, cash, card) with their id, bucket (Essentials, Growth, " +
        "Stability, Rewards), current balance, and archived flag, plus each bucket's total. Use for balance " +
        "questions and to find an account id before searching its activity or staging a transfer.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object([]);

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public string ProgressLabel(AiToolArgs args) => "Checking your accounts";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var accounts = await _accounts.GetAccountsAsync(cancellationToken);
        // Balances reveal nothing in sensitive mode, so the snapshot replay is skipped entirely.
        IReadOnlyDictionary<string, decimal> balances = context.SensitiveMode || accounts.Count == 0
            ? new Dictionary<string, decimal>()
            : await _accounts.GetBalancesAsync(accounts, cancellationToken);

        context.Evidence.RecordAll(AiEvidenceLedger.Account, accounts.Select(account => account.Id));
        return AiToolResult.Of(new
        {
            accounts = accounts.Select(account => new
            {
                id = account.Id,
                name = account.Name,
                bucket = account.Bucket,
                kind = account.Kind,
                archived = account.IsArchived ? true : (bool?)null,
                balance = balances.TryGetValue(account.Id, out var balance) ? balance : (decimal?)null
            }).ToList(),
            buckets = Buckets.Select(bucket => new
            {
                bucket,
                accountCount = accounts.Count(account => account.Bucket == bucket),
                balance = balances.Count == 0
                    ? (decimal?)null
                    : accounts.Where(account => account.Bucket == bucket).Sum(account => balances.GetValueOrDefault(account.Id))
            }).ToList()
        });
    }
}
