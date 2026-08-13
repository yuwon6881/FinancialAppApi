using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Accounts;

public sealed partial class LedgerAccountService
{
    public async Task<LedgerAccountReconcileResult> ReconcileAsync(
        LedgerAccountReconcileRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!FinancialConstants.BudgetCategories.Contains(request.Bucket, StringComparer.OrdinalIgnoreCase))
            return new(LedgerAccountMutationStatus.Invalid, "Choose a valid ledger bucket.");
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Trim().Length > 80)
            return new(LedgerAccountMutationStatus.Invalid, "The reconciliation operation id is invalid.");
        if (request.Targets is null || request.Targets.Count == 0)
            return new(LedgerAccountMutationStatus.Invalid, "At least one account target is required.");
        if (request.Targets.Any(target => !LedgerAccountKind.IsValid(target.Kind)))
            return new(LedgerAccountMutationStatus.Invalid, "Choose a valid account kind for every row.");
        if (request.Targets.Any(target => target.InterestFrequency is not null
                && !LedgerAccountInterestFrequency.IsValid(target.InterestFrequency)))
            return new(LedgerAccountMutationStatus.Invalid, "Choose a valid interest frequency for every row.");
        if (request.Targets.Any(target => target.InterestRatePercent is < 0m or > 100m))
            return new(LedgerAccountMutationStatus.Invalid, "Interest rate must be between 0% and 100%.");
        if (request.Targets.Any(target => target.InterestEnabled == true
                && (target.InterestRatePercent ?? 0m) <= 0m))
            return new(LedgerAccountMutationStatus.Invalid, "Enter an interest rate above 0%, or choose no interest.");

        var bucket = FinancialConstants.BudgetCategories.First(value =>
            value.Equals(request.Bucket, StringComparison.OrdinalIgnoreCase));
        var userId = _context.RequireCurrentUserId();
        var operationKey = SanitizeOperationId(request.OperationId);
        await ApplyDueInterestAsync(cancellationToken);
        var targets = request.Targets
            .Select(target => target with
            {
                Id = string.IsNullOrWhiteSpace(target.Id) ? null : target.Id.Trim(),
                Name = target.Name.Trim(),
                Kind = LedgerAccountKind.Normalize(target.Kind),
                ExpectedCurrent = RoundMoney(target.ExpectedCurrent),
                Target = RoundMoney(target.Target),
                InterestRatePercent = target.InterestRatePercent is null
                    ? null
                    : NormalizeInterestRate(target.InterestRatePercent.Value),
                InterestFrequency = target.InterestFrequency is null
                    ? null
                    : NormalizeInterestFrequency(target.InterestFrequency),
            })
            .ToList();
        if (targets.Count == 0 || targets.Any(target => string.IsNullOrWhiteSpace(target.Name)))
            return new(LedgerAccountMutationStatus.Invalid, "Every account row needs a name.");
        if (targets.Select(target => target.Name.ToLowerInvariant()).Distinct().Count() != targets.Count)
            return new(LedgerAccountMutationStatus.Invalid, "Account names must be unique.");
        if (targets.Where(target => target.Id is not null)
            .GroupBy(target => target.Id!, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
            return new(LedgerAccountMutationStatus.Invalid, "An account cannot appear more than once.");

        await using var databaseTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var accounts = await _context.LedgerAccounts.ToListAsync(cancellationToken);
            var bucketAccounts = accounts.Where(account => account.Bucket.Equals(bucket, StringComparison.OrdinalIgnoreCase)).ToList();
            var operationPrefix = $"reconcile-{operationKey}-";
            var existingOperationTransactions = await _context.Transactions
                .Where(transaction => transaction.UserId == userId && transaction.Id.StartsWith(operationPrefix))
                .ToListAsync(cancellationToken);
            if (existingOperationTransactions.Count > 0)
            {
                await databaseTransaction.CommitAsync(cancellationToken);
                var existingAccounts = await GetAccountsAsync(cancellationToken);
                return new(
                    LedgerAccountMutationStatus.Success,
                    Accounts: existingAccounts,
                    Transactions: existingOperationTransactions.Select(ToReconcileTransaction).ToList());
            }
            var balances = await _balanceService.GetBalancesAsync(accounts, cancellationToken);
            var actualBucketTotal = RoundMoney(bucketAccounts.Sum(account => balances.GetValueOrDefault(account.Id)));
            if (!NearlyEqual(actualBucketTotal, request.ExpectedBucketTotal))
                return await ConflictAsync(databaseTransaction, "The bucket changed while this setup was open.");

            foreach (var target in targets.Where(target => target.Id is not null))
            {
                var existing = bucketAccounts.FirstOrDefault(account => account.Id == target.Id);
                if (existing is null) continue;
                var current = RoundMoney(balances.GetValueOrDefault(existing.Id));
                if (!NearlyEqual(current, target.ExpectedCurrent))
                    return await ConflictAsync(databaseTransaction, "An account balance changed while this setup was open.");
                if (existing.IsArchived && !NearlyEqual(current, target.Target))
                    return await ConflictAsync(databaseTransaction, "Closed accounts cannot receive a new balance. Reopen the account first.");
            }

            var resolvedTargets = new List<LedgerAccountReconcileTarget>(targets.Count);
            foreach (var target in targets)
            {
                var existingById = target.Id is null
                    ? null
                    : accounts.FirstOrDefault(account => account.Id == target.Id);
                if (target.Id is not null && existingById is not null
                    && !existingById.Bucket.Equals(bucket, StringComparison.OrdinalIgnoreCase))
                {
                    return await ConflictAsync(databaseTransaction, "An account cannot be reconciled into another bucket.");
                }
                if (target.Id is not null && existingById is null
                    && await _context.LedgerAccounts
                        .IgnoreQueryFilters()
                        .AnyAsync(account => account.Id == target.Id && account.UserId != userId, cancellationToken))
                {
                    return await ConflictAsync(databaseTransaction, "One of these account rows belongs to another user.");
                }
                LedgerAccount? account = target.Id is not null
                    ? bucketAccounts.FirstOrDefault(candidate => candidate.Id == target.Id)
                    : accounts.FirstOrDefault(candidate => candidate.UserId == userId
                        && candidate.Bucket.Equals(bucket, StringComparison.OrdinalIgnoreCase)
                        && candidate.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
                if (account is null)
                {
                    var interest = NormalizeReconcileInterest(target);
                    account = new LedgerAccount
                    {
                        Id = target.Id ?? $"acct-{Guid.NewGuid():N}",
                        Name = target.Name,
                        Bucket = bucket,
                        Kind = target.Kind,
                        InterestEnabled = interest.Enabled,
                        InterestRatePercent = interest.RatePercent,
                        InterestFrequency = interest.Frequency,
                        InterestNextAccrualDate = interest.Enabled && !target.IsArchived
                            ? LedgerAccountInterestFrequency.NextDate(_clock.Today, interest.Frequency)
                            : null,
                        IsDefault = target.IsDefault,
                        IsArchived = target.IsArchived,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    };
                    _context.LedgerAccounts.Add(account);
                    accounts.Add(account);
                    bucketAccounts.Add(account);
                    if (account.IsDefault && !account.IsArchived)
                    {
                        foreach (var other in bucketAccounts.Where(other => other.Id != account.Id && other.IsDefault))
                            other.IsDefault = false;
                    }
                }
                else
                {
                    var wasArchived = account.IsArchived;
                    account.Name = target.Name;
                    account.Kind = target.Kind;
                    account.IsArchived = target.IsArchived;
                    if (target.InterestEnabled.HasValue
                        || target.InterestRatePercent.HasValue
                        || target.InterestFrequency is not null)
                    {
                        var interest = NormalizeReconcileInterest(target, account);
                        var interestChanged = account.InterestEnabled != interest.Enabled
                            || account.InterestRatePercent != interest.RatePercent
                            || !string.Equals(account.InterestFrequency, interest.Frequency, StringComparison.OrdinalIgnoreCase);
                        account.InterestEnabled = interest.Enabled;
                        account.InterestRatePercent = interest.RatePercent;
                        account.InterestFrequency = interest.Frequency;
                        if (!interest.Enabled || account.IsArchived)
                        {
                            account.InterestNextAccrualDate = null;
                            account.InterestRemainder = 0m;
                        }
                        else if (interestChanged || account.InterestNextAccrualDate is null)
                        {
                            account.InterestNextAccrualDate = LedgerAccountInterestFrequency.NextDate(_clock.Today, interest.Frequency);
                            account.InterestRemainder = 0m;
                        }
                    }
                    if (account.IsArchived)
                    {
                        account.InterestNextAccrualDate = null;
                        account.InterestRemainder = 0m;
                    }
                    else if (wasArchived && account.InterestEnabled && account.InterestNextAccrualDate is null)
                    {
                        account.InterestNextAccrualDate = LedgerAccountInterestFrequency.NextDate(_clock.Today, account.InterestFrequency);
                        account.InterestRemainder = 0m;
                    }
                    account.UpdatedAt = DateTime.UtcNow;
                    if (!target.IsArchived && target.IsDefault)
                    {
                        foreach (var other in bucketAccounts.Where(other => other.Id != account.Id && other.IsDefault))
                            other.IsDefault = false;
                        account.IsDefault = true;
                    }
                    else
                    {
                        account.IsDefault = false;
                    }
                }
                resolvedTargets.Add(target with { Id = account.Id });
            }

            var liveDefault = bucketAccounts
                .Where(account => !account.IsArchived && account.IsDefault)
                .OrderBy(account => account.CreatedAt)
                .FirstOrDefault();
            if (liveDefault is null)
                return await ConflictAsync(databaseTransaction, "Choose a live default account before reconciling.");

            var working = bucketAccounts.ToDictionary(
                account => account.Id,
                account => balances.GetValueOrDefault(account.Id));
            foreach (var target in resolvedTargets.Where(target => !balances.ContainsKey(target.Id!)))
                working[target.Id!] = 0m;

            var targetTotal = RoundMoney(resolvedTargets.Sum(target => target.Target));
            var bucketDelta = RoundMoney(targetTotal - actualBucketTotal);
            var createdTransactions = new List<LedgerAccountReconcileTransaction>();
            if (!NearlyEqual(bucketDelta, 0m))
            {
                var id = $"reconcile-{operationKey}-adjustment";
                var adjustment = await AddOrGetAdjustmentAsync(
                    id,
                    bucket,
                    bucketDelta,
                    liveDefault.Id,
                    cancellationToken);
                createdTransactions.Add(adjustment);
                working[liveDefault.Id] = RoundMoney(working.GetValueOrDefault(liveDefault.Id) + bucketDelta);
            }

            var differences = resolvedTargets
                .Select(target => new
                {
                    AccountId = target.Id!,
                    Difference = RoundMoney(target.Target - working.GetValueOrDefault(target.Id!)),
                })
                .Where(item => !NearlyEqual(item.Difference, 0m))
                .ToList();
            var sourceRemaining = differences
                .Where(item => item.Difference < 0m)
                .ToDictionary(item => item.AccountId, item => item.Difference, StringComparer.Ordinal);
            var destinations = differences.Where(item => item.Difference > 0m).ToList();
            var moveIndex = 0;
            foreach (var destination in destinations)
            {
                var remaining = destination.Difference;
                foreach (var sourceId in sourceRemaining.Keys.ToList())
                {
                    var available = Math.Abs(sourceRemaining[sourceId]);
                    var amount = RoundMoney(Math.Min(remaining, available));
                    if (amount <= 0m) continue;
                    var id = $"reconcile-{operationKey}-move-{moveIndex++}";
                    var move = await AddOrGetAccountMoveAsync(
                        id,
                        amount,
                        sourceId,
                        destination.AccountId,
                        cancellationToken);
                    createdTransactions.Add(move);
                    remaining = RoundMoney(remaining - amount);
                    sourceRemaining[sourceId] = RoundMoney(sourceRemaining[sourceId] + amount);
                    if (NearlyEqual(remaining, 0m)) break;
                }
                if (!NearlyEqual(remaining, 0m))
                    return await ConflictAsync(databaseTransaction, "The requested account amounts cannot be reconciled.");
            }

            await _context.SaveChangesAsync(cancellationToken);
            await databaseTransaction.CommitAsync(cancellationToken);
            if (createdTransactions.Any(transaction =>
                    !string.Equals(transaction.LedgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase)))
            {
                var (year, month) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
                    _clock.Today,
                    await GetCycleDayAsync(cancellationToken));
                await _cycleBalanceService.InvalidateFromAsync(year, month, cancellationToken);
            }
            var finalAccounts = await GetAccountsAsync(cancellationToken);
            return new(LedgerAccountMutationStatus.Success, Accounts: finalAccounts, Transactions: createdTransactions);
        }
        catch
        {
            await databaseTransaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<LedgerAccountReconcileTransaction> AddOrGetAdjustmentAsync(
        string id,
        string bucket,
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
                || existing.CounterAccountId is not null)
                throw new InvalidOperationException("A reconciliation operation id is already used for another transaction.");
            return ToReconcileTransaction(existing);
        }

        var transaction = new Transaction
        {
            Id = id,
            UserId = _context.RequireCurrentUserId(),
            Date = TransactionDate.StartOfDate(_clock.Today),
            PostedAt = DateTime.UtcNow,
            Description = $"Account balance adjustment - {bucket}",
            Category = "Adjustment",
            LedgerCategory = bucket,
            Amount = amount,
            ExcludeFromAutocomplete = true,
            AccountId = accountId,
            StabilityReloadIntent = StabilityReloadIntent.Unanswered,
        };
        _context.Transactions.Add(transaction);
        return ToReconcileTransaction(transaction);
    }

    private async Task<LedgerAccountReconcileTransaction> AddOrGetAccountMoveAsync(
        string id,
        decimal amount,
        string sourceAccountId,
        string destinationAccountId,
        CancellationToken cancellationToken)
    {
        var existing = await _context.Transactions
            .FirstOrDefaultAsync(transaction => transaction.Id == id, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.LedgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.Category, "Transfer", StringComparison.OrdinalIgnoreCase)
                || existing.Amount != amount
                || existing.AccountId != sourceAccountId
                || existing.CounterAccountId != destinationAccountId)
                throw new InvalidOperationException("A reconciliation operation id is already used for another transaction.");
            return ToReconcileTransaction(existing);
        }

        var transaction = new Transaction
        {
            Id = id,
            UserId = _context.RequireCurrentUserId(),
            Date = TransactionDate.StartOfDate(_clock.Today),
            PostedAt = DateTime.UtcNow,
            Description = "Move between accounts",
            Category = "Transfer",
            LedgerCategory = "AccountMove",
            Amount = amount,
            ExcludeFromAutocomplete = true,
            AccountId = sourceAccountId,
            CounterAccountId = destinationAccountId,
            StabilityReloadIntent = StabilityReloadIntent.Unanswered,
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
            transaction.CounterAccountId);

    private static async Task<LedgerAccountReconcileResult> ConflictAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        string message)
    {
        await transaction.RollbackAsync();
        return new(LedgerAccountMutationStatus.Conflict, message);
    }

    private static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

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
