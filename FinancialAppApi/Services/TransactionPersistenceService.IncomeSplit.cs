using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class TransactionPersistenceService
{
    /// <summary>
    /// The settings and fund balance an income split is resolved against.
    /// </summary>
    private sealed record IncomeSplitContext(
        FinancialSetting? Setting,
        Stability.StabilityState? State,
        Stability.StabilityPlanSnapshot? Plan,
        bool PreserveHistoricalRecovery = false);

    private static bool IsIncomeLedgerCategory(string? ledgerCategory) =>
        string.Equals(ledgerCategory, "Income", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrEmpty(ledgerCategory)
            && ledgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Loads what an income split needs, doing all of its I/O up front.
    /// <para>
    /// Callers MUST invoke this before staging any change on the context. Reading the fund balance
    /// goes through <c>GetOpeningBalanceAsync</c>, which can rebuild the cycle-balance cache and
    /// call <c>SaveChangesAsync</c> -- with edits already staged that would commit them early, and
    /// outside the transaction <c>SaveAndInvalidateCycleBalancesAsync</c> opens.
    /// </para>
    /// </summary>
    private async Task<IncomeSplitContext> LoadIncomeSplitContextAsync(
        string? ledgerCategory,
        DateOnly transactionDate,
        DateTime postedAt,
        string transactionId,
        bool preserveHistoricalRecovery,
        CancellationToken cancellationToken)
    {
        if (!IsIncomeLedgerCategory(ledgerCategory)) return new IncomeSplitContext(null, null, null);

        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null) return new IncomeSplitContext(null, null, null);

        // Excludes this transaction's own earlier contribution, so re-saving a salary is measured
        // against the fund without itself rather than on top of it.
        var state = await _stabilityRecoveryService.GetStabilityStateAsync(
            setting, transactionDate, transactionId, cancellationToken);
        var plan = Stability.StabilityPlanRevisionService.At(
            await _stabilityPlanRevisionService.GetAsync(setting, cancellationToken), postedAt);
        return new IncomeSplitContext(setting, state, plan, preserveHistoricalRecovery);
    }

    private static string ResolveIncomeSplitSpec(Transaction transaction, IncomeSplitContext context)
    {
        var isPlainIncome = string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase);
        var isProposedSplit = !string.IsNullOrEmpty(transaction.LedgerCategory)
            && transaction.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase);
        if (!isPlainIncome && !isProposedSplit) return "";

        var setting = context.Setting;
        var state = context.State;
        var stabilityAlloc = context.Plan?.StabilityAlloc ?? setting?.StabilityAlloc ?? 0m;

        // Plain "Income" is the only branch that needs settings -- it has no percentages of its own
        // to fall back on. A proposed split carries its own and must still save without a settings
        // row, which is how it behaved before the cap moved server-side.
        if (setting == null && isPlainIncome) return "";

        if (isPlainIncome)
        {
            // Every caller gets the target cap here, not just the web form. It used to be enforced
            // client-side only, so AI ledger drafts, recurring settlement and any outbox replay
            // whose balance had gone stale applied raw percentages and sailed past the target.
            var requestedTopUp = Math.Max(0m, transaction.StabilityRecoveryTopUpAmount ?? 0m);
            if (context.PreserveHistoricalRecovery && requestedTopUp > 0m)
            {
                return Stability.IncomeSplitPlanner.Resolve(
                    transaction.Amount,
                    setting!.EssentialsAlloc,
                    setting.GrowthAlloc,
                    stabilityAlloc,
                    setting.RewardsAlloc,
                    stabilityBalance: 0m,
                    stabilityTarget: 0m,
                    stabilityOverflowRedirect: setting.StabilityOverflowRedirect,
                    requestedTopUp: requestedTopUp).ToSpecString();
            }
            var maximumTopUp = MaximumRecoveryTopUp(transaction.Amount, setting!, state!, context.Plan);
            var appliedTopUp = Math.Min(requestedTopUp, maximumTopUp);
            // New salaries always record the explicit answer, including zero. Leaving ordinary
            // salary as legacy-null lets a later allocation change reinterpret it as repayment.
            transaction.StabilityRecoveryTopUpAmount = appliedTopUp;
            return Stability.IncomeSplitPlanner.Resolve(
                transaction.Amount,
                setting!.EssentialsAlloc,
                setting.GrowthAlloc,
                stabilityAlloc,
                setting.RewardsAlloc,
                state!.CurrentBalance,
                state.Target,
                setting.StabilityOverflowRedirect,
                requestedTopUp: appliedTopUp).ToSpecString();
        }

        var proposedSpec = transaction.LedgerCategory!["IncomeSplit:".Length..];
        transaction.LedgerCategory = "Income";
        if (!Stability.IncomeSplitSpec.TryParseSpecString(proposedSpec, out var proposed))
        {
            return "";
        }

        // Clamped, never rejected: a proposal can arrive from an offline replay whose balance moved
        // before the queue drained, and rejecting it would fail a salary the user cannot re-enter.
        // The excess lands wherever StabilityOverflowRedirect points, which is that setting's job.
        if (setting == null || state == null)
        {
            var (fallback, _) = Stability.IncomeSplitPlanner.ClampProposed(
                proposed, transaction.Amount, 0m, 0m, setting?.StabilityOverflowRedirect);
            return fallback.ToSpecString();
        }

        var baselineStability = Math.Max(0m, stabilityAlloc);
        var proposedTopUp = transaction.StabilityRecoveryTopUpAmount
            ?? Math.Max(0m, transaction.Amount * (proposed.Stability - baselineStability));
        var applied = Math.Min(proposedTopUp, MaximumRecoveryTopUp(transaction.Amount, setting, state, context.Plan));
        transaction.StabilityRecoveryTopUpAmount = applied;
        return Stability.IncomeSplitPlanner.Resolve(
            transaction.Amount,
            setting.EssentialsAlloc,
            setting.GrowthAlloc,
            stabilityAlloc,
            setting.RewardsAlloc,
            state.CurrentBalance,
            state.Target,
            setting.StabilityOverflowRedirect,
            applied).ToSpecString();
    }

    private static decimal MaximumRecoveryTopUp(
        decimal incomeAmount,
        FinancialSetting setting,
        Stability.StabilityState state,
        Stability.StabilityPlanSnapshot? plan = null)
    {
        if (incomeAmount <= 0m) return 0m;
        var stabilityAlloc = plan?.StabilityAlloc ?? setting.StabilityAlloc;
        var normalStability = incomeAmount * Math.Max(0m, stabilityAlloc);
        var targetRoomAfterNormal = state.Target > 0m
            ? Math.Max(0m, state.Target - state.CurrentBalance - normalStability)
            : decimal.MaxValue;
        var otherShare = Math.Max(0m,
            setting.EssentialsAlloc + setting.GrowthAlloc + setting.RewardsAlloc);
        var capacity = Math.Min(
            state.OutstandingObligation,
            Math.Min(targetRoomAfterNormal, incomeAmount * otherShare));
        return Math.Floor(Math.Max(0m, capacity) * 100m) / 100m;
    }

    private static bool TryDecodeRecoveryTopUp(string? encoded, out decimal? value)
    {
        value = null;
        if (encoded == null) return true;
        if (!ObfuscationHelper.TryDeobfuscate(encoded, out var decoded) || decoded < 0m) return false;
        value = Math.Round(decoded, 2, MidpointRounding.AwayFromZero);
        return true;
    }

    private async Task AddIncomeSplitTransactionsAsync(
        Transaction transaction,
        string splitSpec,
        IReadOnlyDictionary<string, string>? splitAccountIds,
        CancellationToken cancellationToken)
    {
        if (!IsIncomeLedgerCategory(transaction.LedgerCategory)) return;
        // The parent becomes a persisted Income row, which has no bucket leg. Every generated
        // child receives its account from the explicit four-bucket placement map.
        transaction.AccountId = null;
        transaction.CounterAccountId = null;

        if (string.IsNullOrEmpty(splitSpec)) return;

        var parts = splitSpec.Split(',');
        if (parts.Length != 4) return;

        var categories = FinancialConstants.BudgetCategories;
        var percentages = new decimal[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!decimal.TryParse(
                    parts[i],
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out percentages[i]) || percentages[i] < 0)
            {
                return;
            }
        }

        var percentageTotal = percentages.Sum();
        if (percentageTotal <= 0) return;

        // Allocate in integer cents using the largest-remainder method. Rounding each bucket
        // independently can make the generated rows differ from the salary by one or more cents.
        var totalCents = decimal.ToInt64(transaction.Amount * 100m);
        var allocations = percentages
            .Select((percentage, index) =>
            {
                var exactCents = totalCents * percentage / percentageTotal;
                var floorCents = decimal.ToInt64(decimal.Floor(exactCents));
                return new { Index = index, Cents = floorCents, Fraction = exactCents - floorCents };
            })
            .ToArray();

        var allocatedCents = allocations.Sum(allocation => allocation.Cents);
        var remainingCents = totalCents - allocatedCents;
        var remainderOrder = allocations
            .OrderByDescending(allocation => allocation.Fraction)
            .ThenBy(allocation => allocation.Index)
            .Select(allocation => allocation.Index)
            .ToArray();
        var finalCents = allocations.Select(allocation => allocation.Cents).ToArray();
        for (long i = 0; i < remainingCents; i++)
        {
            finalCents[remainderOrder[(int)(i % remainderOrder.Length)]]++;
        }

        for (var i = 0; i < finalCents.Length; i++)
        {
            if (finalCents[i] <= 0) continue;
            var bucket = categories[i];

            var split = new Transaction
            {
                Id = $"{transaction.Id}-split-{bucket}",
                Date = transaction.Date,
                PostedAt = transaction.PostedAt,
                Description = $"[Split: {bucket}] {transaction.Description}",
                Category = "Transfer",
                LedgerCategory = $"Transfer:Income->{bucket}",
                Amount = finalCents[i] / 100m,
                ExcludeFromAutocomplete = true,
                AccountId = splitAccountIds![bucket],
            };
            _context.Transactions.Add(split);
        }
    }

    private async Task<TransactionMutationResult?> ValidateIncomeSplitAccountIdsAsync(
        string ledgerCategory,
        IReadOnlyDictionary<string, string>? splitAccountIds,
        CancellationToken cancellationToken)
    {
        if (!IsIncomeLedgerCategory(ledgerCategory)) return null;

        var missingBuckets = FinancialConstants.BudgetCategories
            .Where(bucket => splitAccountIds is null
                || !splitAccountIds.TryGetValue(bucket, out var accountId)
                || string.IsNullOrWhiteSpace(accountId))
            .ToArray();
        if (missingBuckets.Length > 0)
            return InvalidAccount(
                "Choose an open account for every income bucket.",
                code: "ledger_account_required",
                missingBuckets: missingBuckets);

        var requested = FinancialConstants.BudgetCategories
            .Select(bucket => splitAccountIds![bucket].Trim())
            .ToArray();
        var rows = await _context.LedgerAccounts
            .AsNoTracking()
            .Where(account => requested.Contains(account.Id))
            .ToListAsync(cancellationToken);
        var invalidBuckets = FinancialConstants.BudgetCategories
            .Where(bucket =>
            {
                var accountId = splitAccountIds![bucket].Trim();
                var account = rows.FirstOrDefault(row => row.Id == accountId);
                return account is null
                    || account.IsArchived
                    || !account.Bucket.Equals(bucket, StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        if (invalidBuckets.Length > 0)
            return InvalidAccount(
                "Each income split must use an open account in its matching bucket.",
                code: "ledger_account_invalid",
                missingBuckets: invalidBuckets);

        return null;
    }
}
