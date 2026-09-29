namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    // An account a proposed draft may be placed in. Placement checks only existence, bucket, and
    // archived state, so no balance is carried.
    internal sealed record AiLedgerAccountRow(
        string Id,
        string Name,
        string Bucket,
        string Kind,
        bool IsArchived);

    internal sealed record AiLedgerAccountContext(IReadOnlyList<AiLedgerAccountRow> Accounts);
}
