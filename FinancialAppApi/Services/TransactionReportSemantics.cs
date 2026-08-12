using FinancialAppApi.Models;

namespace FinancialAppApi.Services;

public static class TransactionReportSemantics
{
    public static bool IsTransfer(Transaction transaction) =>
        IsTransfer(transaction.Category, transaction.LedgerCategory);

    public static bool IsTransfer(string? category, string? ledgerCategory) =>
        string.Equals(category, "Transfer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ledgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase) ||
        (ledgerCategory?.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ?? false);

    public static bool IsBalanceAdjustment(Transaction transaction) =>
        IsBalanceAdjustment(transaction.Category);

    public static bool IsBalanceAdjustment(string? category) =>
        string.Equals(category, "Adjustment", StringComparison.OrdinalIgnoreCase);

    public static bool IsDiscarded(string? ledgerCategory) =>
        string.Equals(ledgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase);

    public static bool IsReportableCashMovement(Transaction transaction) =>
        IsReportableCashMovement(transaction.Amount, transaction.Category, transaction.LedgerCategory);

    public static bool IsReportableCashMovement(
        decimal amount,
        string? category,
        string? ledgerCategory) =>
        amount != 0m &&
        !IsTransfer(category, ledgerCategory) &&
        !IsBalanceAdjustment(category) &&
        !IsDiscarded(ledgerCategory);

    public static bool IsReportableInflow(Transaction transaction) =>
        transaction.Amount > 0m && IsReportableCashMovement(transaction);

    public static bool IsReportableOutflow(Transaction transaction) =>
        transaction.Amount < 0m && IsReportableCashMovement(transaction);

    public static bool IsIncomeLedgerCategory(string? ledgerCategory) =>
        string.Equals(ledgerCategory, "Income", StringComparison.OrdinalIgnoreCase) ||
        (ledgerCategory?.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase) ?? false);

    public static bool IsReportableIncome(Transaction transaction) =>
        IsReportableIncome(transaction.Amount, transaction.Category, transaction.LedgerCategory);

    public static bool IsReportableIncome(
        decimal amount,
        string? category,
        string? ledgerCategory) =>
        amount > 0m &&
        IsIncomeLedgerCategory(ledgerCategory) &&
        IsReportableCashMovement(amount, category, ledgerCategory);
}
