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

        var strategy = _context.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
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
                    var existingAccounts = await GetAccountsAsync(cancellationToken);
                    await databaseTransaction.CommitAsync(cancellationToken);
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
                            IsArchived = target.IsArchived,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow,
                        };
                        _context.LedgerAccounts.Add(account);
                        accounts.Add(account);
                        bucketAccounts.Add(account);
                    }
                    else
                    {
                        var wasArchived = account.IsArchived;
                        if (!wasArchived && target.IsArchived)
                        {
                            if (await _context.RecurringPayments.AnyAsync(
                                    payment => payment.AccountId == account.Id && payment.Active,
                                    cancellationToken))
                                return await ConflictAsync(databaseTransaction, "An active recurring payment uses this account. Reassign it before closing the account.");
                            if (!bucketAccounts.Any(other => other.Id != account.Id && !other.IsArchived))
                                return await ConflictAsync(databaseTransaction, "Every bucket needs one open account. Add another before closing this one.");
                        }
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
                    }
                    resolvedTargets.Add(target with { Id = account.Id });
                }

                if (!bucketAccounts.Any(account => !account.IsArchived))
                    return await ConflictAsync(databaseTransaction, "Every bucket needs one open account.");

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
                    var adjustmentAccountId = request.AdjustmentAccountId?.Trim();
                    var adjustmentAccount = adjustmentAccountId is null
                        ? null
                        : bucketAccounts.FirstOrDefault(account => account.Id == adjustmentAccountId);
                    if (adjustmentAccount is null || adjustmentAccount.IsArchived)
                        return await ConflictAsync(databaseTransaction, "Choose an open account in this bucket for the total correction.");
                    var id = $"reconcile-{operationKey}-adjustment";
                    var adjustment = await AddOrGetAdjustmentAsync(
                        id,
                        bucket,
                        bucketDelta,
                        adjustmentAccount.Id,
                        cancellationToken);
                    createdTransactions.Add(adjustment);
                    working[adjustmentAccount.Id] = RoundMoney(working.GetValueOrDefault(adjustmentAccount.Id) + bucketDelta);
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
                var finalAccounts = await GetAccountsAsync(cancellationToken);
                await databaseTransaction.CommitAsync(cancellationToken);
                return new LedgerAccountReconcileResult(
                    LedgerAccountMutationStatus.Success,
                    Accounts: finalAccounts,
                    Transactions: createdTransactions);
            }
            catch
            {
                await databaseTransaction.RollbackAsync(cancellationToken);
                throw;
            }
        });

        if (result.Status == LedgerAccountMutationStatus.Success
            && result.Transactions?.Any(transaction =>
                !string.Equals(transaction.LedgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase)) == true)
        {
            var (year, month) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
                _clock.Today,
                await GetCycleDayAsync(cancellationToken));
            await _cycleBalanceService.InvalidateFromAsync(year, month, cancellationToken);
        }

        return result;
    }

}
