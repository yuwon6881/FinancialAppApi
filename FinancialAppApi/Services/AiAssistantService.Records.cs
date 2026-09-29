namespace FinancialAppApi.Services;

// Row and cycle shapes shared by the action rules and the pure calculators the tools reuse
// (forecasts, anomaly and duplicate checks, purchase cadence).
public partial class AiAssistantService
{
    internal sealed record CycleKey(int Year, int MonthIndex);

    internal sealed record AiTransactionRow(
        string Id,
        DateTime Timestamp,
        string Date,
        string Description,
        string Category,
        string LedgerCategory,
        decimal Amount,
        DateTime? PostedAt = null,
        string? RecurringPaymentId = null,
        string? AccountId = null,
        string? CounterAccountId = null);

    private sealed record AiTransactionDbRow(
        string Id,
        DateTime Date,
        DateTime PostedAt,
        string Description,
        string Category,
        string LedgerCategory,
        decimal Amount,
        string? RecurringPaymentId,
        string? AccountId,
        string? CounterAccountId);

    internal sealed record AiWishlistRow(int Id, string Name, decimal Price, string Priority, bool IsActive, bool IsPurchased, DateTime CreatedAt, DateTime? PurchasedAt = null);

    // What action validation may rely on: the user's categories and the records a tool surfaced
    // this turn, re-read from the database. A record absent here cannot be targeted.
    private sealed record AiContext(
        bool SensitiveMode,
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> LedgerCategories,
        IReadOnlyList<AiTransactionRow> RecentTransactions,
        IReadOnlyList<AiRecurringRow> RecurringPayments,
        IReadOnlyList<AiWishlistRow> WishlistItems,
        AiLedgerAccountContext? LedgerAccountContext);

    private static AiTransactionRow ToAiTransactionRow(AiTransactionDbRow row) => new(
        row.Id,
        row.Date,
        Database.TransactionDate.ToDateOnly(row.Date).ToString("yyyy-MM-dd"),
        row.Description,
        row.Category,
        row.LedgerCategory,
        row.Amount,
        row.PostedAt,
        row.RecurringPaymentId,
        row.AccountId,
        row.CounterAccountId);
}
