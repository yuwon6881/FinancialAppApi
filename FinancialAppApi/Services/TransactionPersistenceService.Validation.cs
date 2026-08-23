using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class TransactionPersistenceService
{
    private static TransactionMutationResult InvalidDate(string? message = null)
    {
        return new TransactionMutationResult(
            TransactionMutationStatus.InvalidDate,
            Message: message ?? "Date must be in yyyy-MM-dd format.");
    }

    private static DateTime ResolvePostedAt(string? value)
    {
        if (DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            var utc = parsed.UtcDateTime;
            if (utc >= DateTime.UnixEpoch && utc <= DateTime.UtcNow.AddDays(1))
            {
                return utc;
            }
        }

        return DateTime.UtcNow;
    }

    private static TransactionMutationResult InvalidAmount(string message = "Amount is malformed.") => new(
        TransactionMutationStatus.InvalidAmount,
        Message: message);

    private async Task<bool> TransactionCategoryExistsAsync(
        string category,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        if (TransactionCategoryService.IsReservedName(category))
        {
            return true;
        }

        return await _context.TransactionCategories.AnyAsync(
            c => c.Name.ToLower() == category.Trim().ToLower(),
            cancellationToken);
    }

    private static TransactionMutationResult InvalidCategory(string category)
    {
        var name = string.IsNullOrWhiteSpace(category) ? "Category" : $"Category '{category}'";
        return new TransactionMutationResult(
            TransactionMutationStatus.InvalidCategory,
            Message: $"{name} does not exist.");
    }

    private static TransactionMutationResult InvalidLedgerCategory(string message)
    {
        return new TransactionMutationResult(
            TransactionMutationStatus.InvalidLedgerCategory,
            Message: message);
    }

    private static TransactionMutationResult InvalidAccount(
        string message,
        string code = "ledger_account_invalid",
        IReadOnlyList<string>? missingBuckets = null) => new(
        TransactionMutationStatus.InvalidAccount,
        Message: message,
        Code: code,
        MissingBuckets: missingBuckets);

    private async Task<TransactionMutationResult?> ValidateAccountReferencesAsync(
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        var accountId = NormalizeOptionalId(transaction.AccountId);
        var counterAccountId = NormalizeOptionalId(transaction.CounterAccountId);
        transaction.AccountId = accountId;
        transaction.CounterAccountId = counterAccountId;

        if (string.Equals(transaction.LedgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase))
        {
            if (accountId is null || counterAccountId is null || accountId == counterAccountId)
                return InvalidAccount(
                    "An account move needs two different accounts.",
                    "ledger_account_required");

            var accountMoveRows = await _context.LedgerAccounts
                .Where(account => account.Id == accountId || account.Id == counterAccountId)
                .ToListAsync(cancellationToken);
            if (accountMoveRows.Count != 2 || accountMoveRows.Any(account => account.IsArchived))
                return InvalidAccount("Both accounts must be open before money can be moved between them.");
            if (!string.Equals(accountMoveRows[0].Bucket, accountMoveRows[1].Bucket, StringComparison.OrdinalIgnoreCase))
                return InvalidAccount("Both accounts in an account move must belong to the same bucket.");
            return null;
        }

        if (LedgerAccountPlacement.IsBucket(transaction.LedgerCategory) && accountId is null)
            return InvalidAccount(
                "Choose the account that holds this bucket's money.",
                "ledger_account_required",
                [transaction.LedgerCategory]);

        if (counterAccountId is not null
            && !transaction.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
            return InvalidAccount("A counter account is only valid for a bucket transfer or account move.");
        if (transaction.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
        {
            var transferParts = transaction.LedgerCategory["Transfer:".Length..].Split("->", StringSplitOptions.None);
            if (transferParts.Length == 2)
            {
                var source = transferParts[0].Trim();
                var target = transferParts[1].Trim();
                if (LedgerAccountPlacement.IsBucket(source) && accountId is null)
                    return InvalidAccount(
                        "Choose the account sending the money.",
                        "ledger_account_required",
                        [source]);
                if (LedgerAccountPlacement.IsBucket(target)
                    && (source.Equals("Income", StringComparison.OrdinalIgnoreCase) ? accountId is null : counterAccountId is null))
                    return InvalidAccount(
                        "Choose the account receiving the money.",
                        "ledger_account_required",
                        [target]);
            }
        }
        if (accountId is null && counterAccountId is null) return null;
        if (accountId is not null && counterAccountId is not null && accountId == counterAccountId)
            return InvalidAccount("The source and destination accounts must be different.");

        var accountIds = new[] { accountId, counterAccountId }
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var accountRows = await _context.LedgerAccounts
            .Where(account => accountIds.Contains(account.Id))
            .ToListAsync(cancellationToken);
        if (accountRows.Count != accountIds.Count || accountRows.Any(account => account.IsArchived))
            return InvalidAccount("The selected account is not available.");
        if (LedgerAccountPlacement.IsBucket(transaction.LedgerCategory)
            && accountRows.Any(account => !account.Bucket.Equals(transaction.LedgerCategory, StringComparison.OrdinalIgnoreCase)))
            return InvalidAccount("The selected account must belong to the transaction's ledger bucket.");

        if (transaction.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = transaction.LedgerCategory["Transfer:".Length..].Split("->", StringSplitOptions.None);
            if (parts.Length == 2)
            {
                var source = parts[0].Trim();
                var target = parts[1].Trim();
                var account = accountRows.FirstOrDefault(row => row.Id == accountId);
                var counter = accountRows.FirstOrDefault(row => row.Id == counterAccountId);
                if (LedgerAccountPlacement.IsBucket(source)
                    && (account is null || !account.Bucket.Equals(source, StringComparison.OrdinalIgnoreCase)))
                    return InvalidAccount("The sending account must belong to the source bucket.");
                if (LedgerAccountPlacement.IsBucket(target)
                    && !source.Equals("Income", StringComparison.OrdinalIgnoreCase)
                    && (counter is null || !counter.Bucket.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    return InvalidAccount("The receiving account must belong to the target bucket.");
                if (source.Equals("Income", StringComparison.OrdinalIgnoreCase)
                    && (account is null || !account.Bucket.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    return InvalidAccount("The receiving account must belong to the target bucket.");
            }
        }
        return null;
    }

    private static string? NormalizeOptionalId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ResolveUpdatedAccountId(
        string? requestedId,
        string? existingId,
        string existingLedgerCategory,
        string nextLedgerCategory,
        bool counter)
    {
        var explicitId = NormalizeOptionalId(requestedId);
        if (explicitId is not null) return explicitId;
        if (string.Equals(existingLedgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nextLedgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase))
            return existingId;

        var existingBucket = AccountLegBucket(existingLedgerCategory, counter);
        var nextBucket = AccountLegBucket(nextLedgerCategory, counter);
        return string.Equals(existingBucket, nextBucket, StringComparison.OrdinalIgnoreCase)
            ? existingId
            : null;
    }

    private static string? AccountLegBucket(string ledgerCategory, bool counter)
    {
        if (LedgerAccountPlacement.IsBucket(ledgerCategory)) return counter ? null : ledgerCategory;
        if (!ledgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = ledgerCategory["Transfer:".Length..].Split("->", StringSplitOptions.None);
        if (parts.Length != 2) return null;
        var source = parts[0].Trim();
        var target = parts[1].Trim();
        if (source.Equals("Income", StringComparison.OrdinalIgnoreCase))
            return counter ? null : target;
        return counter ? target : source;
    }

    private static (bool IsValid, string Category, string LedgerCategory, string? Message)
        ValidateAndNormalizeLedgerCategory(string category, string ledgerCategory, decimal amount)
    {
        var normalizedCategory = category.Trim();
        var normalizedLedger = ledgerCategory?.Trim() ?? string.Empty;
        var isTransferCategory = normalizedCategory.Equals("Transfer", StringComparison.OrdinalIgnoreCase);

        if (normalizedLedger.Equals("AccountMove", StringComparison.OrdinalIgnoreCase))
        {
            if (!isTransferCategory)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "An account move must use the Transfer category.");
            }
            if (amount <= 0)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "Account move amount must be greater than zero.");
            }
            return (true, "Transfer", "AccountMove", null);
        }

        if (normalizedLedger.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
        {
            if (!isTransferCategory)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "A transfer ledger route must use the Transfer category.");
            }

            var route = normalizedLedger["Transfer:".Length..].Split("->", StringSplitOptions.None);
            if (route.Length != 2)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "Transfer ledger category must use the format Transfer:Source->Target.");
            }

            var source = FinancialConstants.BudgetCategories.FirstOrDefault(bucket =>
                bucket.Equals(route[0].Trim(), StringComparison.OrdinalIgnoreCase));
            var target = FinancialConstants.BudgetCategories.FirstOrDefault(bucket =>
                bucket.Equals(route[1].Trim(), StringComparison.OrdinalIgnoreCase));

            if (source == null || target == null)
            {
                return (false, normalizedCategory, normalizedLedger,
                    $"Transfer source and target must be one of: {string.Join(", ", FinancialConstants.BudgetCategories)}.");
            }
            if (source == target)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "Transfer source and target must be different.");
            }
            if (amount <= 0)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "Transfer amount must be greater than zero.");
            }

            return (true, "Transfer", $"Transfer:{source}->{target}", null);
        }

        if (isTransferCategory)
        {
            return (false, normalizedCategory, normalizedLedger,
                "The Transfer category requires a valid Transfer:Source->Target ledger route.");
        }

        if (normalizedLedger.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = normalizedLedger["IncomeSplit:".Length..].Split(',');
            var percentages = new decimal[4];
            var validPercentages = parts.Length == 4;
            for (var i = 0; validPercentages && i < parts.Length; i++)
            {
                validPercentages = decimal.TryParse(
                    parts[i],
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out percentages[i]) && percentages[i] >= 0;
            }
            if (!validPercentages || Math.Abs(percentages.Sum() - 100m) > 0.01m)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "Income split must contain four non-negative percentages totaling 100.");
            }
            if (amount <= 0)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "Income amount must be greater than zero.");
            }

            return (true, normalizedCategory,
                $"IncomeSplit:{string.Join(',', percentages.Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)))}", null);
        }

        var plainLedger = new[] { "Income", "Discarded" }
            .Concat(FinancialConstants.BudgetCategories)
            .FirstOrDefault(value => value.Equals(normalizedLedger, StringComparison.OrdinalIgnoreCase));
        if (plainLedger == null)
        {
            return (false, normalizedCategory, normalizedLedger, "Ledger category is not recognized.");
        }
        if (plainLedger == "Income" && amount <= 0)
        {
            return (false, normalizedCategory, normalizedLedger, "Income amount must be greater than zero.");
        }
        if (plainLedger == "Discarded" && amount != 0)
        {
            return (false, normalizedCategory, normalizedLedger, "Discarded transactions must have a zero amount.");
        }

        return (true, normalizedCategory, plainLedger, null);
    }
}
