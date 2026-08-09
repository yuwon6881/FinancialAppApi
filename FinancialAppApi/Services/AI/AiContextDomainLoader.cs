using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private sealed record ReferenceDomainContext(
        object RecurringContext,
        List<AiRecurringRow> RecurringRows,
        object WishlistContext,
        List<AiWishlistRow> WishlistRows);

    private async Task<ReferenceDomainContext> LoadReferenceDomainContextAsync(
        AiQueryPlan queryPlan,
        bool sensitiveMode,
        CancellationToken cancellationToken)
    {
        object recurringContext = Array.Empty<object>();
        var recurringRows = new List<AiRecurringRow>();
        if (queryPlan.NeedsRecurring)
        {
            recurringRows = await LoadRecurringRowsAsync(cancellationToken);
            recurringRows = DetectRecurringStatus(queryPlan.QueryText) switch
            {
                "active" => recurringRows.Where(row => row.Active).ToList(),
                "inactive" => recurringRows.Where(row => !row.Active).ToList(),
                _ => recurringRows
            };
            recurringContext = sensitiveMode
                ? recurringRows.Select(row => new
                {
                    row.Id, row.Name, row.Category, row.LedgerCategory,
                    row.StartDate, row.EndDate, row.DueDate, row.Active, row.Frequency, row.NextDueDate,
                    row.PushReminderEnabled, row.PushReminderMode, row.PushReminderLeadDays
                }).ToList()
                : recurringRows;
        }

        object wishlistContext = Array.Empty<object>();
        var wishlistRows = new List<AiWishlistRow>();
        if (queryPlan.NeedsWishlist)
        {
            wishlistRows = await LoadWishlistRowsAsync(queryPlan.WishlistItemId, cancellationToken);
            wishlistRows = DetectWishlistStatus(queryPlan.QueryText) switch
            {
                "active" => wishlistRows.Where(row => row.IsActive && !row.IsPurchased).ToList(),
                "inactive" => wishlistRows.Where(row => !row.IsActive && !row.IsPurchased).ToList(),
                "purchased" => wishlistRows.Where(row => row.IsPurchased).ToList(),
                "unpurchased" => wishlistRows.Where(row => !row.IsPurchased).ToList(),
                _ => wishlistRows
            };
            wishlistContext = sensitiveMode
                ? wishlistRows.Select(row => new
                {
                    row.Id, row.Name, row.Priority, row.IsActive, row.IsPurchased, row.CreatedAt, row.PurchasedAt
                }).ToList()
                : wishlistRows;
        }

        return new ReferenceDomainContext(recurringContext, recurringRows, wishlistContext, wishlistRows);
    }

    private sealed record TransactionDomainContext(
        List<AiTransactionRow> Transactions,
        bool ScopeTruncated,
        int? ExactMatchCount,
        IReadOnlyList<string> ExcludedCategories,
        IReadOnlyList<string> ExcludedLedgerCategories,
        IReadOnlyList<string> IncludedCategories,
        IReadOnlyList<string> IncludedLedgerCategories,
        string? RequestedTransactionType,
        bool AppliesTransactionTypeFilter,
        bool CycleTotalRecoverable,
        IReadOnlyDictionary<string, bool> CycleHasAnyRows);

    private async Task<TransactionDomainContext> LoadTransactionDomainContextAsync(
        AiIntentPlan intentPlan,
        TargetCycleSelection targetSelection,
        int cycleDay,
        DateOnly? exactDate,
        IReadOnlyList<string> categories,
        CancellationToken cancellationToken)
    {
        var queryPlan = intentPlan.QueryPlan;
        var transactions = new List<AiTransactionRow>();
        var scopeTruncated = false;
        int? exactMatchCount = null;
        var cycleHasAnyRows = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var cycle in targetSelection.Cycles)
        {
            var cycleRange = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
            var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(cycleRange.start));
            var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(cycleRange.end));
            cycleHasAnyRows[FormatCycleKey(cycle)] = await ScopedTransactions(start, end, null)
                .AnyAsync(cancellationToken);
        }
        if (queryPlan.NeedsTransactionDetail || queryPlan.NeedsCycleSummary)
        {
            if (targetSelection.Cycles.Count > 0)
            {
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var range in MergeCycleRanges(targetSelection.Cycles, cycleDay))
                {
                    if (queryPlan.TransactionData == TransactionDataLevel.MatchingRows && !string.IsNullOrWhiteSpace(queryPlan.SearchText))
                    {
                        exactMatchCount = (exactMatchCount ?? 0) + await CountTransactionsAsync(range.Start, range.End, queryPlan.SearchText, queryPlan.TransactionIds, cancellationToken);
                    }
                    var rows = await QueryTransactionsAsync(
                        range.Start,
                        range.End,
                        queryPlan.TransactionData == TransactionDataLevel.MatchingRows ? queryPlan.SearchText : null,
                        queryPlan.TransactionIds,
                        cancellationToken);
                    if (rows.Count > MaxTransactionsPerRange)
                    {
                        scopeTruncated = true;
                        rows = rows.Take(MaxTransactionsPerRange).ToList();
                    }
                    foreach (var row in rows)
                    {
                        if (seenIds.Add(row.Id)) transactions.Add(row);
                    }
                }
            }
            else
            {
                transactions.AddRange(await LoadRecentFallbackTransactionsAsync(
                    queryPlan.TransactionData == TransactionDataLevel.MatchingRows ? queryPlan.SearchText : null,
                    queryPlan.TransactionIds,
                    cancellationToken));
            }
        }

        transactions = transactions
            .OrderByDescending(row => row.Timestamp)
            .ThenByDescending(row => row.PostedAt ?? row.Timestamp)
            .ThenByDescending(row => row.Id)
            .ToList();
        if (exactDate.HasValue)
        {
            transactions = transactions.Where(row => DateOnly.FromDateTime(row.Timestamp) == exactDate.Value).ToList();
            if (exactMatchCount.HasValue) exactMatchCount = transactions.Count;
        }

        var constraints = intentPlan.Constraints;
        var (excludedCategories, excludedLedgerCategories) = ResolveConstraintCategories(
            constraints.ExcludedCategories, categories, LedgerCategories);
        var (includedCategories, includedLedgerCategories) = ResolveConstraintCategories(
            constraints.IncludedCategories, categories, LedgerCategories);
        if (constraints.ExcludeTransfers || excludedCategories.Count > 0 || excludedLedgerCategories.Count > 0)
        {
            transactions = transactions.Where(row =>
                    !(constraints.ExcludeTransfers && IsTransfer(row)) &&
                    !excludedCategories.Contains(row.Category, StringComparer.OrdinalIgnoreCase) &&
                    !excludedLedgerCategories.Contains(row.LedgerCategory, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        if (includedCategories.Count > 0 || includedLedgerCategories.Count > 0)
        {
            transactions = transactions.Where(row =>
                    includedCategories.Contains(row.Category, StringComparer.OrdinalIgnoreCase) ||
                    includedLedgerCategories.Contains(row.LedgerCategory, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        var requestedTransactionType = DetectTransactionType(queryPlan.QueryText);
        var facets = DetectQueryFacets(queryPlan.QueryText);
        var appliesTransactionTypeFilter = requestedTransactionType != null &&
            !facets.Contains("daily_extreme", StringComparer.Ordinal) &&
            (facets.Contains("list", StringComparer.Ordinal) || facets.Contains("activity_count", StringComparer.Ordinal));
        if (appliesTransactionTypeFilter)
        {
            transactions = transactions.Where(row => requestedTransactionType switch
            {
                "inflow" => row.Amount > 0 && !IsTransfer(row),
                "outflow" => row.Amount < 0 && !IsTransfer(row),
                "transfer" => IsTransfer(row),
                _ => true
            }).ToList();
            if (exactMatchCount.HasValue) exactMatchCount = transactions.Count;
        }

        var hasPostQueryFilters = exactDate.HasValue || constraints.ExcludeTransfers ||
            excludedCategories.Count > 0 || excludedLedgerCategories.Count > 0 ||
            includedCategories.Count > 0 || includedLedgerCategories.Count > 0 ||
            appliesTransactionTypeFilter;
        if (exactMatchCount.HasValue && hasPostQueryFilters)
            exactMatchCount = scopeTruncated ? null : transactions.Count;

        var cycleTotalRecoverable = targetSelection.Cycles.Count > 0
            && excludedCategories.Count == 0
            && excludedLedgerCategories.Count == 0
            && includedCategories.Count == 0
            && includedLedgerCategories.Count == 0
            && (!appliesTransactionTypeFilter || requestedTransactionType == "outflow");

        return new TransactionDomainContext(
            transactions,
            scopeTruncated,
            exactMatchCount,
            excludedCategories,
            excludedLedgerCategories,
            includedCategories,
            includedLedgerCategories,
            requestedTransactionType,
            appliesTransactionTypeFilter,
            cycleTotalRecoverable,
            cycleHasAnyRows);
    }

    private sealed record LedgerDomainContext(
        object? StabilityProgress,
        int? AffordableWishlistCount,
        object? LedgerBalanceForecast,
        object? WishlistForecast);

    private async Task<LedgerDomainContext> BuildLedgerDomainContextAsync(
        AiIntentPlan intentPlan,
        Models.FinancialSetting? setting,
        TransactionDomainContext transactionDomain,
        TargetCycleSelection targetSelection,
        IReadOnlyList<AiWishlistRow> wishlistRows,
        int selectedYear,
        int selectedMonthIndex,
        int cycleDay,
        bool sensitiveMode,
        (string Ledger, decimal Target)? ledgerForecastRequest,
        CancellationToken cancellationToken)
    {
        var queryPlan = intentPlan.QueryPlan;
        var wantsAffordableCount = queryPlan.NeedsWishlist &&
            System.Text.RegularExpressions.Regex.IsMatch(queryPlan.QueryText, @"\b(how many|afford|can i (?:buy|afford|get))\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var wantsStabilityProgress = System.Text.RegularExpressions.Regex.IsMatch(queryPlan.QueryText,
            @"\bstability\b.{0,40}\b(fund|goal|target|on track|progress|reach(?:ed)?|close|there yet|percent)\b|\b(goal|target|on track|progress|reach(?:ed)?|percent)\b.{0,40}\bstability\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var needsTransactions = !sensitiveMode &&
            (queryPlan.NeedsWishlistForecast || queryPlan.NeedsRewards || queryPlan.NeedsReport ||
             wantsAffordableCount || wantsStabilityProgress || ledgerForecastRequest != null);
        var filtered = queryPlan.TransactionData == TransactionDataLevel.MatchingRows &&
            !string.IsNullOrWhiteSpace(queryPlan.SearchText) || transactionDomain.AppliesTransactionTypeFilter ||
            transactionDomain.ExcludedCategories.Count > 0 || transactionDomain.ExcludedLedgerCategories.Count > 0 ||
            transactionDomain.IncludedCategories.Count > 0 || transactionDomain.IncludedLedgerCategories.Count > 0;
        var ledgerTransactions = transactionDomain.Transactions;
        if (needsTransactions && filtered && targetSelection.Cycles.Count > 0)
        {
            ledgerTransactions = await LoadUnfilteredCycleTransactionsAsync(targetSelection.Cycles, cycleDay, cancellationToken);
        }

        decimal freeRewards = 0m;
        decimal requiredRewardsPerCycle = 0m;
        object? stabilityProgress = null;
        int? affordableWishlistCount = null;
        object? ledgerBalanceForecast = null;
        if (needsTransactions)
        {
            var opening = await new CycleBalanceService(_context).GetOpeningBalanceAsync(selectedYear, selectedMonthIndex, cycleDay);
            var rewardsPool = await _savingsGoalService.GetPoolSummaryAsync(cancellationToken);
            var activeTransactions = ledgerTransactions
                .Where(row => IsInCycle(row, new CycleKey(selectedYear, selectedMonthIndex), cycleDay))
                .ToList();
            decimal LedgerNet(string ledgerCategory) => activeTransactions.Sum(row => CategoryAttributionService.GetCategoryAmount(new Models.Transaction
            {
                Amount = row.Amount,
                LedgerCategory = row.LedgerCategory
            }, ledgerCategory));
            // Wishlist affordability is about money that is free to spend, not the whole Rewards
            // envelope. SavingsGoalService is the authority for both values, so this cannot drift
            // from the Rewards page when earmarks are added or released.
            freeRewards = rewardsPool.Unassigned;
            requiredRewardsPerCycle = rewardsPool.RequiredPerCycleTotal;
            if (wantsStabilityProgress)
            {
                var balance = opening.stability + LedgerNet("Stability");
                var target = setting?.TargetStabilityFund ?? 0m;
                stabilityProgress = new
                {
                    currentStabilityBalance = balance,
                    targetStabilityFund = target,
                    percentReached = target > 0 ? Math.Round(balance / target * 100m, 1) : (decimal?)null,
                    remaining = target > 0 ? Math.Max(0m, target - balance) : (decimal?)null
                };
            }
            if (wantsAffordableCount)
            {
                affordableWishlistCount = wishlistRows.Count(item => !item.IsPurchased && freeRewards >= item.Price);
            }
            if (ledgerForecastRequest is { } forecast)
            {
                var openingBalance = forecast.Ledger switch
                {
                    "Essentials" => opening.essentials,
                    "Growth" => opening.growth,
                    "Stability" => opening.stability,
                    "Rewards" => opening.rewards,
                    _ => 0m
                };
                ledgerBalanceForecast = BuildLedgerBalanceForecast(ComputeLedgerBalanceForecast(
                    forecast.Ledger,
                    forecast.Target,
                    openingBalance + LedgerNet(forecast.Ledger),
                    ledgerTransactions,
                    targetSelection.Cycles,
                    cycleDay,
                    _financialClock.LocalNow));
            }
        }

        var activeCycleStart = CategoryAttributionService.GetCycleRange(selectedYear, selectedMonthIndex, cycleDay).start;
        var wishlistReference = queryPlan.SearchText ?? intentPlan.ConversationState.LastWishlistReference;
        var wishlistForecast = queryPlan.NeedsWishlistForecast && !sensitiveMode
            ? BuildWishlistForecast(new WishlistForecastPolicy(
                wishlistRows,
                ledgerTransactions,
                targetSelection.Cycles,
                cycleDay,
                activeCycleStart,
                _financialClock.LocalNow,
                wishlistReference,
                freeRewards,
                requiredRewardsPerCycle))
            : null;
        return new LedgerDomainContext(stabilityProgress, affordableWishlistCount, ledgerBalanceForecast, wishlistForecast);
    }

    private async Task<object?> BuildBalanceSnapshotAsync(
        AiQueryPlan queryPlan,
        IReadOnlyList<AiTransactionRow> transactions,
        int selectedYear,
        int selectedMonthIndex,
        int cycleDay,
        bool sensitiveMode)
    {
        if (sensitiveMode || !System.Text.RegularExpressions.Regex.IsMatch(queryPlan.QueryText,
                @"\b(wallet balance|ledger (?:category )?(?:balance|balances)|most balance|highest balance|balance right now)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return null;
        var opening = await new CycleBalanceService(_context).GetOpeningBalanceAsync(selectedYear, selectedMonthIndex, cycleDay);
        var active = transactions.Where(row => IsInCycle(row, new CycleKey(selectedYear, selectedMonthIndex), cycleDay)).ToList();
        decimal Net(string category) => active.Sum(row => CategoryAttributionService.GetCategoryAmount(new Models.Transaction
        {
            Amount = row.Amount,
            LedgerCategory = row.LedgerCategory
        }, category));
        var balances = new[]
        {
            new { ledgerCategory = "Essentials", balance = opening.essentials + Net("Essentials") },
            new { ledgerCategory = "Growth", balance = opening.growth + Net("Growth") },
            new { ledgerCategory = "Stability", balance = opening.stability + Net("Stability") },
            new { ledgerCategory = "Rewards", balance = opening.rewards + Net("Rewards") }
        };
        return new
        {
            walletBalance = balances.Where(balance => balance.ledgerCategory != "Growth").Sum(balance => balance.balance),
            ledgerBalances = balances,
            highestLedgerBalance = balances.OrderByDescending(balance => balance.balance).First()
        };
    }

    private sealed record RecurringDomainInsights(object? BillStatus, object? Upcoming, object? CostSummary);

    private async Task<RecurringDomainInsights> BuildRecurringDomainInsightsAsync(
        AiQueryPlan queryPlan,
        IReadOnlyList<AiRecurringRow> recurringRows,
        TargetCycleSelection targetSelection,
        int selectedYear,
        int selectedMonthIndex,
        int cycleDay,
        bool sensitiveMode,
        CancellationToken cancellationToken)
    {
        object? billStatus = null;
        if (queryPlan.NeedsRecurring && System.Text.RegularExpressions.Regex.IsMatch(queryPlan.QueryText,
                @"\b(discard|discarded|skip|skipped|unpaid|not paid|missed|paid|pending|overdue|due|status)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var cycles = targetSelection.Cycles.Count > 0
                ? targetSelection.Cycles
                : [new CycleKey(selectedYear, selectedMonthIndex)];
            var statuses = await LoadRecurringBillStatusesAsync(cycles, cycleDay, cancellationToken);
            billStatus = statuses.Select(status => sensitiveMode
                ? (object)new { status.Id, status.Name, status.Category, status.LedgerCategory, status.DueDate, status.Status }
                : new { status.Id, status.Name, status.Category, status.LedgerCategory, status.DueDate, status.Status, status.Amount }).ToList();
        }
        object? upcoming = null;
        if (queryPlan.Metrics.Contains(DerivedMetric.RecurringUpcoming) && recurringRows.Count > 0)
        {
            upcoming = recurringRows.Where(row => row.Active)
                .OrderBy(row => DateTime.TryParse(row.NextDueDate, out var due) ? due : DateTime.MaxValue)
                .Take(10)
                .Select(row => sensitiveMode
                    ? (object)new { row.Name, row.Category, row.LedgerCategory, row.Frequency, nextDueDate = row.NextDueDate }
                    : new { row.Name, row.Category, row.LedgerCategory, row.Frequency, nextDueDate = row.NextDueDate, amount = Math.Abs(row.Amount) })
                .ToList();
        }
        var costSummary = queryPlan.NeedsRecurring && !sensitiveMode && recurringRows.Count > 0 && WantsRecurringCostSummary(queryPlan.QueryText)
            ? BuildRecurringCostSummary(recurringRows)
            : null;
        return new RecurringDomainInsights(billStatus, upcoming, costSummary);
    }
}
