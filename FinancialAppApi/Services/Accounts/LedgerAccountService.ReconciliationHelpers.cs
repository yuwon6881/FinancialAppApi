using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Accounts;

public sealed partial class LedgerAccountService
{
    private async Task<LedgerAccountReconcileTransaction> AddOrGetAdjustmentAsync(
        string id,
        string bucket,
        string? baseDescription,
        string accountName,
        decimal amount,
        string accountId,
        CancellationToken cancellationToken)
    {
        var existing = await _context.Transactions
            .FirstOrDefaultAsync(transaction => transaction.Id == id, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.LedgerCategory, bucket, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.Category, "Adjustment", StringComparison.OrdinalIgnoreCase)
                || existing.Amount != amount
                || existing.AccountId != accountId
                || existing.CounterAccountId is not null
                || !existing.IsAccountBalanceAdjustment)
                throw new InvalidOperationException("A reconciliation operation id is already used for another transaction.");
            return ToReconcileTransaction(existing);
        }

        var transaction = new Transaction
        {
            Id = id,
            UserId = _context.RequireCurrentUserId(),
            Date = TransactionDate.StartOfDate(_clock.Today),
            PostedAt = DateTime.UtcNow,
            Description = $"{(string.IsNullOrWhiteSpace(baseDescription) ? "Account balance adjustment" : baseDescription.Trim())} - {accountName}",
            Category = "Adjustment",
            LedgerCategory = bucket,
            Amount = amount,
            ExcludeFromAutocomplete = true,
            IsAccountBalanceAdjustment = true,
            AccountId = accountId,
            StabilityReloadIntent = StabilityReloadIntent.NotRequired,
        };
        _context.Transactions.Add(transaction);
        return ToReconcileTransaction(transaction);
    }

    private static LedgerAccountReconcileTransaction ToReconcileTransaction(Transaction transaction) =>
        new(
            transaction.Id,
            transaction.Date,
            transaction.Description,
            transaction.Category,
            transaction.LedgerCategory,
            transaction.Amount,
            transaction.AccountId,
            transaction.CounterAccountId,
            transaction.IsAccountBalanceAdjustment,
            transaction.StabilityReloadIntent);

    private static async Task<LedgerAccountReconcileResult> ConflictAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        string message)
    {
        await transaction.RollbackAsync();
        return new(LedgerAccountMutationStatus.Conflict, message);
    }

    private static decimal RoundMoney(decimal value) =>
        MoneyRounding.RoundMoney(value);

    private static (bool Enabled, decimal RatePercent, string Frequency) NormalizeReconcileInterest(
        LedgerAccountReconcileTarget target,
        LedgerAccount? existing = null)
    {
        var enabled = target.InterestEnabled ?? existing?.InterestEnabled ?? false;
        var rate = target.InterestRatePercent ?? existing?.InterestRatePercent ?? 0m;
        var frequency = target.InterestFrequency
            ?? existing?.InterestFrequency
            ?? LedgerAccountInterestFrequency.Monthly;
        return (enabled, NormalizeInterestRate(rate), NormalizeInterestFrequency(frequency));
    }

    private static bool NearlyEqual(decimal left, decimal right) =>
        Math.Abs(left - right) < 0.005m;

    private static string SanitizeOperationId(string value)
    {
        var safe = new string(value
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .Take(60)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "operation" : safe;
    }
}
