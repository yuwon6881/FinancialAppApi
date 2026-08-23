using System.Text.RegularExpressions;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private sealed record ContextDatasets(
        List<object> CycleSummaries,
        object DerivedMetrics,
        object? Rewards = null,
        object? Investments = null,
        object? ReportReview = null);

    private async Task<ContextDatasets> BuildContextDatasetsAsync(
        AiQueryPlan queryPlan,
        Models.FinancialSetting? setting,
        AiIntentPlan intentPlan,
        List<AiTransactionRow> allTransactions,
        TargetCycleSelection targetSelection,
        int selectedYear,
        int selectedMonthIndex,
        int cycleDay,
        bool sensitiveMode,
        int? exactMatchCount,
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow,
        AiPurchaseFrequencyMetric? purchaseFrequency,
        List<AiRecurringRow> recurringRows,
        List<AiWishlistRow> wishlistRows,
        LedgerDomainContext ledgerDomain,
        AiLedgerAccountContext ledgerAccountContext,
        CancellationToken cancellationToken)
    {
        // Built after recovery so a per-cycle exact outflow (when recovered) replaces the
        // truncated sample sum in both the single-cycle summary and the multi-cycle comparison.
        var cycleSummaries = queryPlan.NeedsCycleSummary
            ? BuildCycleSummaries(allTransactions, targetSelection.Cycles, cycleDay, !sensitiveMode, perCycleRecoveredOutflow)
            : new List<object>();
        var balanceSnapshot = await BuildBalanceSnapshotAsync(
            queryPlan, allTransactions, selectedYear, selectedMonthIndex, cycleDay, sensitiveMode);
        var recurringInsights = await BuildRecurringDomainInsightsAsync(
            queryPlan, recurringRows, targetSelection, selectedYear, selectedMonthIndex, cycleDay, sensitiveMode, cancellationToken);
        var recurringBillStatus = recurringInsights.BillStatus;
        var recurringUpcoming = recurringInsights.Upcoming;
        var recurringCostSummary = recurringInsights.CostSummary;

        var extraMetrics = new Dictionary<string, object?>();
        if (purchaseFrequency != null) extraMetrics["purchaseFrequency"] = purchaseFrequency;
        if (recurringUpcoming != null) extraMetrics["upcomingBills"] = recurringUpcoming;
        if (ledgerDomain.StabilityProgress != null) extraMetrics["stabilityProgress"] = ledgerDomain.StabilityProgress;
        if (ledgerDomain.AffordableWishlistCount != null) extraMetrics["affordableWishlistCount"] = ledgerDomain.AffordableWishlistCount;
        if (ledgerDomain.LedgerBalanceForecast != null) extraMetrics["ledgerBalanceForecast"] = ledgerDomain.LedgerBalanceForecast;
        if (recurringCostSummary != null) extraMetrics["recurringCostSummary"] = recurringCostSummary;
        if (ledgerAccountContext.Activity != null) extraMetrics["accountActivity"] = ledgerAccountContext.Activity;

        var derivedMetrics = BuildDerivedMetrics(queryPlan, allTransactions, targetSelection.Cycles, cycleDay, sensitiveMode, exactMatchCount, perCycleRecoveredOutflow, balanceSnapshot, recurringBillStatus, extraMetrics);

        var rewards = await BuildRewardsContextAsync(
            queryPlan,
            setting,
            targetSelection.Cycles,
            cycleDay,
            allTransactions,
            wishlistRows,
            ledgerDomain.WishlistForecast,
            sensitiveMode,
            cancellationToken);
        var investments = await BuildInvestmentContextAsync(intentPlan, sensitiveMode, cancellationToken);
        var reportReview = BuildReportReviewContext(queryPlan, targetSelection.Cycles, allTransactions, cycleDay, sensitiveMode);

        return new ContextDatasets(cycleSummaries, derivedMetrics, rewards, investments, reportReview);
    }

    private static object BuildDerivedMetrics(
        AiQueryPlan queryPlan,
        IReadOnlyList<AiTransactionRow> transactions,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        bool sensitiveMode,
        int? exactMatchCount,
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow,
        object? balanceSnapshot = null,
        object? recurringBillStatus = null,
        IReadOnlyDictionary<string, object?>? extraMetrics = null)
    {
        var metrics = new Dictionary<string, object?>();
        if (recurringBillStatus != null) metrics["recurringBillStatus"] = recurringBillStatus;
        if (extraMetrics != null)
        {
            foreach (var kv in extraMetrics) metrics[kv.Key] = kv.Value;
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.ActivityCount) || queryPlan.Metrics.Contains(DerivedMetric.MerchantMatches))
        {
            metrics["transactionMatches"] = new
            {
                query = queryPlan.SearchText,
                count = exactMatchCount ?? transactions.Count,
                totalOutflow = sensitiveMode ? (decimal?)null : Math.Abs(transactions.Where(t => t.Amount < 0 && TransactionReportSemantics.IsReportableCashMovement(t.Amount, t.Category, t.LedgerCategory)).Sum(t => t.Amount)),
                complete = queryPlan.TransactionData == TransactionDataLevel.MatchingRows && (exactMatchCount.HasValue || transactions.Count < MaxTransactionsPerRange),
                    rows = sensitiveMode
                        ? transactions.Take(120).Select(t => (object)new { t.Id, t.Date, t.Description, t.Category }).ToList()
                        : transactions.Take(120).Select(t => (object)new { t.Id, t.Date, t.Description, t.Category, t.Amount }).ToList()
            };
        }
        if (balanceSnapshot != null) metrics["balanceSnapshot"] = balanceSnapshot;
        // Amount-threshold filter ("which transaction exceeded 250", "purchases over 100",
        // "anything between 50 and 200"). Amount-based, so suppressed in sensitive mode like the
        // other magnitude metrics.
        var threshold = TryParseAmountThreshold(queryPlan.QueryText);
        if (!sensitiveMode && threshold != null)
        {
            metrics["thresholdMatches"] = BuildThresholdMatches(
                transactions, threshold, sampleWasComplete: transactions.Count < MaxTransactionsPerRange);
        }
        // Superlative single-record ranking ("biggest transaction", "largest spending", "smallest
        // purchase", "biggest deposit"). Exact over the loaded rows, transfers excluded, spending,
        // all cash inflows, and ordinary income ranked separately -- so the model never has to
        // eyeball a mixed sample. Amount-based, so suppressed in sensitive mode like the other
        // magnitude metrics.
        if (!sensitiveMode
            && (queryPlan.NeedsTransactionDetail || queryPlan.NeedsCycleSummary)
            && WantsTopTransactions(queryPlan.QueryText))
        {
            metrics["topTransactions"] = BuildTopTransactions(transactions, queryPlan.QueryText);
        }
        if (Regex.IsMatch(queryPlan.QueryText, @"\b(which|what) day\b.*\b(most|highest|largest)\b|\bmost\b.*\b(day|daily)\b", RegexOptions.IgnoreCase))
        {
            var daily = transactions.Where(t => TransactionReportSemantics.IsReportableCashMovement(t.Amount, t.Category, t.LedgerCategory))
                .GroupBy(t => t.Date)
                .Select(g => new
                {
                    date = g.Key,
                    inflow = g.Where(t => t.Amount > 0).Sum(t => t.Amount),
                    outflow = Math.Abs(g.Where(t => t.Amount < 0).Sum(t => t.Amount)),
                    income = g.Where(t => TransactionReportSemantics.IsReportableIncome(t.Amount, t.Category, t.LedgerCategory)).Sum(t => t.Amount)
                }).ToList();
            metrics["dailyExtremes"] = sensitiveMode || daily.Count == 0 ? null : new
            {
                highestInflowDay = daily.OrderByDescending(d => d.inflow).First(),
                highestIncomeDay = daily.OrderByDescending(d => d.income).First(),
                highestOutflowDay = daily.OrderByDescending(d => d.outflow).First()
            };
        }
        if (queryPlan.TransactionIds.Count > 0)
        {
            var referenced = transactions
                .Where(t => queryPlan.TransactionIds.Contains(t.Id, StringComparer.Ordinal))
                .ToList();
            var outflows = referenced.Where(t => t.Amount < 0 && TransactionReportSemantics.IsReportableCashMovement(t.Amount, t.Category, t.LedgerCategory)).ToList();
            var highest = outflows.OrderByDescending(t => Math.Abs(t.Amount)).FirstOrDefault();
            metrics["referencedTransactions"] = new
            {
                ids = referenced.Select(t => t.Id).ToList(),
                count = referenced.Count,
                totalCost = Math.Abs(outflows.Sum(t => t.Amount)),
                highest = highest == null ? null : new
                {
                    highest.Id,
                    highest.Description,
                    highest.Date,
                    amount = sensitiveMode ? (decimal?)null : Math.Abs(highest.Amount)
                },
                complete = referenced.Count == queryPlan.TransactionIds.Count
            };
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.CycleComparison))
        {
            metrics["cycleComparison"] = BuildCycleComparison(transactions, cycles, cycleDay, perCycleRecoveredOutflow);
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.AnomalyDetection))
        {
            metrics["anomalies"] = DetectAnomalies(transactions)
                .Select(a => sensitiveMode
                    ? (object)new { a.Id, a.Description, a.Category, amount = (decimal?)null, a.Score, a.Reason }
                    : new { a.Id, a.Description, a.Category, amount = (decimal?)a.Amount, a.Score, a.Reason })
                .ToList();
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.DuplicateDetection))
        {
            metrics["duplicateCandidates"] = DetectDuplicates(transactions)
                .Select(d => sensitiveMode
                    ? (object)new { d.Ids, d.Description, d.DaysApart, d.Confidence, d.Reasons, amount = (decimal?)null }
                    : new { d.Ids, d.Description, d.DaysApart, d.Confidence, d.Reasons, amount = (decimal?)d.Amount })
                .ToList();
        }
        return metrics;
    }

    private static object BuildCycleComparison(
        IReadOnlyList<AiTransactionRow> transactions,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow)
    {
        return cycles.Select(cycle =>
        {
            var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
            var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
            var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
            var txs = transactions.Where(t => t.Timestamp >= start && t.Timestamp < end && TransactionReportSemantics.IsReportableCashMovement(t.Amount, t.Category, t.LedgerCategory)).ToList();
            // Prefer the recovered exact SUM for this specific cycle when the sample was
            // truncated; otherwise the in-memory sample sum is already complete.
            var recovered = perCycleRecoveredOutflow != null && perCycleRecoveredOutflow.ContainsKey(cycle);
            var outflow = recovered ? perCycleRecoveredOutflow![cycle] : Math.Abs(txs.Where(t => t.Amount < 0).Sum(t => t.Amount));
            var inflow = txs.Where(t => t.Amount > 0).Sum(t => t.Amount);
            var income = txs
                .Where(t => TransactionReportSemantics.IsReportableIncome(t.Amount, t.Category, t.LedgerCategory))
                .Sum(t => t.Amount);
            return new
            {
                month = FinancialConstants.MonthAbbreviations[cycle.MonthIndex - 1],
                year = cycle.Year,
                income,
                inflow,
                otherInflow = inflow - income,
                outflow,
                net = inflow - outflow,
                exactOutflow = recovered
            };
        }).ToList();
    }

    private static List<object> BuildCycleSummaries(
        IReadOnlyList<AiTransactionRow> transactions,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        bool includeAmounts,
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow)
    {
        var summaries = new List<object>();
        foreach (var cycle in cycles)
        {
            var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
            var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
            var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
            var txs = transactions
                .Where(t => t.Timestamp >= start && t.Timestamp < end)
                .ToList();
            var nonTransferTxs = txs.Where(t => TransactionReportSemantics.IsReportableCashMovement(t.Amount, t.Category, t.LedgerCategory)).ToList();

            if (!includeAmounts)
            {
                summaries.Add(new
                {
                    month = FinancialConstants.MonthAbbreviations[cycle.MonthIndex - 1],
                    year = cycle.Year,
                    label = range.label,
                    transactionCount = txs.Count,
                    categoryCounts = txs
                        .GroupBy(t => t.Category)
                        .Select(g => new { category = g.Key, count = g.Count() })
                        .OrderByDescending(x => x.count)
                        .Take(8)
                        .ToList(),
                    ledgerCounts = txs
                        .GroupBy(t => t.LedgerCategory)
                        .Select(g => new { ledgerCategory = g.Key, count = g.Count() })
                        .OrderByDescending(x => x.count)
                        .Take(8)
                        .ToList(),
                    recentTransactions = txs
                        .OrderByDescending(t => t.Timestamp)
                        .ThenByDescending(t => t.PostedAt ?? t.Timestamp)
                        .Take(12)
                        .Select(t => new
                        {
                            t.Id,
                            t.Date,
                            t.Description,
                            t.Category,
                            t.LedgerCategory,
                            txType = IsTransfer(t) ? "transfer" : t.Amount < 0 ? "outflow" : "inflow"
                        })
                        .ToList()
                });
                continue;
            }

            var categorySpend = nonTransferTxs
                .Where(t => t.Amount < 0)
                .GroupBy(t => t.Category)
                .Select(g => new { category = g.Key, outflow = Math.Abs(g.Sum(t => t.Amount)) })
                .OrderByDescending(x => x.outflow)
                .Take(8)
                .ToList();

            // Ledger net must be summed over ALL cycle transactions (transfers included), exactly
            // like the authoritative FinancialService/CycleBalanceService: GetCategoryAmount is
            // what routes a Transfer:Source->Target row into the -source/+target ledgers. Summing
            // over nonTransferTxs instead dropped every transfer, so ledgers funded by transfers
            // (typically Growth and Stability) wrongly showed a net of 0 / understated balances.
            var ledgerNet = LedgerCategories
                .Where(c => c != "Income")
                .Select(c => new
                {
                    ledgerCategory = c,
                        net = txs.Sum(t => CategoryAttributionService.GetCategoryAmount(new Models.Transaction
                    {
                        Amount = t.Amount,
                        LedgerCategory = t.LedgerCategory
                    }, c))
                })
                .ToList();

            var recoveredExact = perCycleRecoveredOutflow != null && perCycleRecoveredOutflow.ContainsKey(cycle);
            var inflow = nonTransferTxs.Where(t => t.Amount > 0).Sum(t => t.Amount);
            var income = nonTransferTxs
                .Where(t => TransactionReportSemantics.IsReportableIncome(t.Amount, t.Category, t.LedgerCategory))
                .Sum(t => t.Amount);
            var otherInflowByCategory = nonTransferTxs
                .Where(t => t.Amount > 0 && !TransactionReportSemantics.IsReportableIncome(t.Amount, t.Category, t.LedgerCategory))
                .GroupBy(t => t.Category)
                .Select(g => new { category = g.Key, amount = g.Sum(t => t.Amount) })
                .OrderByDescending(x => x.amount)
                .Take(8)
                .ToList();
            var outflow = recoveredExact ? perCycleRecoveredOutflow![cycle] : Math.Abs(nonTransferTxs.Where(t => t.Amount < 0).Sum(t => t.Amount));
            summaries.Add(new
            {
                month = FinancialConstants.MonthAbbreviations[cycle.MonthIndex - 1],
                year = cycle.Year,
                label = range.label,
                transactionCount = txs.Count,
                income,
                inflow,
                otherInflow = inflow - income,
                otherInflowByCategory,
                outflow,
                netChange = inflow - outflow,
                // True when this cycle's outflow/netChange came from an exact database SUM
                // rather than the (possibly truncated) in-memory sample.
                exactOutflow = recoveredExact,
                categorySpend,
                ledgerNet,
                largestTransactions = txs
                    .OrderByDescending(t => Math.Abs(t.Amount))
                    .Take(10)
                    .Select(t => new
                    {
                        t.Id,
                        t.Date,
                        t.Description,
                        t.Category,
                        t.LedgerCategory,
                        t.Amount,
                        txType = IsTransfer(t) ? "transfer" : t.Amount < 0 ? "outflow" : "inflow"
                    })
                    .ToList()
            });
        }
        return summaries;
    }
}
