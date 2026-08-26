using System.Globalization;
using System.Linq.Expressions;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class TransactionQueryService
{
    internal static IQueryable<Transaction> ApplyAllFilters(
        IQueryable<Transaction> query,
        bool useIlike,
        string? search,
        string? searchMode,
        string? ledgerCategory,
        string? category,
        string? txType,
        string? startDate,
        string? endDate,
        decimal? minAmount,
        decimal? maxAmount,
        bool recurringOnly,
        bool wishlistOnly,
        string? recurringFilter,
        string? wishlistFilter,
        string? reloadFilter = null,
        string? accountId = null)
    {
        // Case-insensitive to match the writer-side OrdinalIgnoreCase checks in
        // TransactionPersistenceService: a row stored as "discarded" is soft-deleted there and
        // must not reappear here.
        query = query.Where(t => t.LedgerCategory.ToLower() != "discarded");

        if (!string.IsNullOrWhiteSpace(accountId))
        {
            var accountIds = accountId.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (accountIds.Count > 0)
            {
                query = query.Where(t =>
                    (t.AccountId != null && accountIds.Contains(t.AccountId)) ||
                    (t.CounterAccountId != null && accountIds.Contains(t.CounterAccountId)));
            }
        }

        if (TransactionDate.TryParseInputDate(startDate, out var startDateOnly))
        {
            query = query.Where(t => t.Date >= TransactionDate.StartOfDate(startDateOnly));
        }
        if (TransactionDate.TryParseInputDate(endDate, out var endDateOnly))
        {
            query = query.Where(t => t.Date < TransactionDate.ExclusiveEndOfDate(endDateOnly));
        }

        // Compare the raw signed amount against ±bound instead of Math.Abs(Amount): the
        // abs() form is non-sargable (the planner cannot use an index on Amount), whereas
        // the OR/AND range form is. Semantics are identical for the inflow(+)/outflow(-)
        // sign convention.
        if (minAmount is >= 0)
        {
            var min = minAmount.Value;
            query = query.Where(t => t.Amount >= min || t.Amount <= -min);
        }
        if (maxAmount is >= 0)
        {
            var max = maxAmount.Value;
            query = query.Where(t => t.Amount <= max && t.Amount >= -max);
        }

        var recurringMode = NormalizeLinkFilter(recurringFilter, recurringOnly);
        if (recurringMode == "only")
        {
            query = query.Where(t => t.RecurringPaymentId != null && t.RecurringPaymentId != "");
        }
        else if (recurringMode == "exclude")
        {
            query = query.Where(t => t.RecurringPaymentId == null || t.RecurringPaymentId == "");
        }

        var wishlistMode = NormalizeLinkFilter(wishlistFilter, wishlistOnly);
        if (wishlistMode == "only")
        {
            query = query.Where(t => t.WishlistItemId != null);
        }
        else if (wishlistMode == "exclude")
        {
            query = query.Where(t => t.WishlistItemId == null);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = TransactionTextSearch.Apply(query, useIlike, search, searchMode);
        }

        if (!string.IsNullOrWhiteSpace(ledgerCategory))
        {
            var buckets = ledgerCategory.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.Trim().ToLower())
                .Where(b => b.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Expression<Func<Transaction, bool>>? bucketPredicate = null;
            foreach (var bucket in buckets)
            {
                var token = bucket;
                // Contains subsumes equality, and the substring match is deliberate: bucket
                // Growth also claims both legs of Transfer:Growth->Rewards. Income is the
                // exception, matching the IncomeSplit: prefix rather than any route that merely
                // mentions income.
                Expression<Func<Transaction, bool>> clause = token == "income"
                    ? t => t.LedgerCategory.ToLower() == "income"
                        || t.LedgerCategory.ToLower().StartsWith("incomesplit:")
                    : t => t.LedgerCategory.ToLower().Contains(token);
                bucketPredicate = bucketPredicate == null
                    ? clause
                    : QueryPredicate.Or(bucketPredicate, clause);
            }

            if (bucketPredicate != null) query = query.Where(bucketPredicate);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var cats = category.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim().ToLower()).ToList();
            query = query.Where(t => cats.Contains(t.Category.ToLower()));
        }

        if (!string.IsNullOrWhiteSpace(txType))
        {
            var types = txType.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().ToLowerInvariant())
                .ToHashSet();

            var hasInflow = types.Contains("inflow");
            var hasOutflow = types.Contains("outflow");
            var hasTransfer = types.Contains("transfer");
            var selected = (hasInflow ? 1 : 0) + (hasOutflow ? 1 : 0) + (hasTransfer ? 1 : 0);

            // Selecting all three types is the same as selecting none. Counting the recognized
            // tokens rather than the raw list means an unknown token cannot pad the count to
            // three and silently disable the filter.
            if (selected is > 0 and < 3)
            {
                // A structural row moves money without being spend or income. AccountMove is
                // named here as well as its Transfer category, which validation already forces,
                // so the filter agrees with the CSV export's classification either way.
                Expression<Func<Transaction, bool>> isStructural = t =>
                    t.Category.ToLower() == "transfer" ||
                    t.LedgerCategory.ToLower().StartsWith("transfer:") ||
                    t.LedgerCategory.ToLower() == "accountmove";

                Expression<Func<Transaction, bool>> isCash = t =>
                    t.Category.ToLower() != "transfer" &&
                    t.Category.ToLower() != "adjustment" &&
                    !t.LedgerCategory.ToLower().StartsWith("transfer:") &&
                    t.LedgerCategory.ToLower() != "accountmove" &&
                    t.LedgerCategory.ToLower() != "discarded";

                Expression<Func<Transaction, bool>>? predicate = null;
                if (hasInflow && hasOutflow) predicate = QueryPredicate.And(t => t.Amount != 0, isCash);
                else if (hasInflow) predicate = QueryPredicate.And(t => t.Amount > 0, isCash);
                else if (hasOutflow) predicate = QueryPredicate.And(t => t.Amount < 0, isCash);

                if (hasTransfer)
                {
                    predicate = predicate == null
                        ? isStructural
                        : QueryPredicate.Or(predicate, isStructural);
                }

                if (predicate != null) query = query.Where(predicate);
            }
        }

        var reloadMode = NormalizeReloadFilter(reloadFilter);
        if (reloadMode == "not-required")
        {
            query = query.Where(t =>
                !t.IsAccountBalanceAdjustment &&
                t.StabilityReloadIntent != null && t.StabilityReloadIntent.ToLower() == "notrequired" &&
                ((t.LedgerCategory.ToLower() == "stability" && t.Amount < 0) ||
                 t.LedgerCategory.ToLower().StartsWith("transfer:stability->")));
        }
        else if (reloadMode != "all")
        {
            query = query.Where(t =>
                !t.IsAccountBalanceAdjustment &&
                // Missing/Unanswered is fail-safe Required everywhere else in the Stability
                // replay. Only an explicit NotRequired may opt a drawdown out.
                (t.StabilityReloadIntent == null || t.StabilityReloadIntent.ToLower() != "notrequired") &&
                ((t.LedgerCategory.ToLower() == "stability" && t.Amount < 0) ||
                 t.LedgerCategory.ToLower().StartsWith("transfer:stability->")));
        }

        return query;
    }

    private async Task<IQueryable<Transaction>> ApplyDerivedReloadStatusFilterAsync(
        IQueryable<Transaction> query,
        string? reloadFilter,
        CancellationToken cancellationToken)
    {
        var mode = NormalizeReloadFilter(reloadFilter);
        if (mode is not ("needs-put-back" or "outstanding" or "partly-repaid" or "complete"))
            return query;

        var openStatuses = await _stabilityReloadStatusService.GetOpenStatusMapAsync(cancellationToken);
        var matchingOpenIds = openStatuses
            .Where(pair => mode switch
            {
                "outstanding" => pair.Value == StabilityReloadStatus.Outstanding,
                "partly-repaid" => pair.Value == StabilityReloadStatus.PartlyRepaid,
                _ => true,
            })
            .Select(pair => pair.Key)
            .ToArray();

        if (mode == "complete")
        {
            var openIds = openStatuses.Keys.ToArray();
            return query.Where(transaction => !openIds.Contains(transaction.Id));
        }

        return query.Where(transaction => matchingOpenIds.Contains(transaction.Id));
    }

    private static string NormalizeReloadFilter(string? filter) =>
        filter?.Trim().ToLowerInvariant() switch
        {
            "put-back" or "required" or "1" => "put-back",
            "needs-put-back" => "needs-put-back",
            "outstanding" => "outstanding",
            "partly-repaid" => "partly-repaid",
            "complete" => "complete",
            "not-required" => "not-required",
            _ => "all",
        };

    private static string NormalizeLinkFilter(string? filter, bool legacyOnly) =>
        filter?.Trim().ToLowerInvariant() switch
        {
            "only" => "only",
            "exclude" => "exclude",
            _ => legacyOnly ? "only" : "all"
        };

    /// <summary>
    /// Ordering, with a total tie-breaker so offset paging cannot drop or repeat a row.
    ///
    /// The Id tie-break must compare ordinally. Postgres would otherwise use the column's
    /// database collation, which weights punctuation differently from the ordinal comparison the
    /// client and the in-memory cycle projection both use, so rows sharing Date and PostedAt
    /// could straddle a page boundary. Collating at the query level keeps the three comparators
    /// in agreement without rebuilding the table's indexes.
    /// </summary>
    internal static IOrderedQueryable<Transaction> ApplySort(
        IQueryable<Transaction> query,
        string? sort,
        bool useOrdinalCollation = false)
    {
        Expression<Func<Transaction, string>> id = useOrdinalCollation
            ? t => EF.Functions.Collate(t.Id, "C")
            : t => t.Id;

        return sort switch
        {
            "date-asc" => query
                .OrderBy(t => t.Date)
                .ThenBy(t => t.PostedAt)
                .ThenBy(id),
            "amount-desc" => query
                .OrderByDescending(t => t.Amount < 0 ? -t.Amount : t.Amount)
                .ThenByDescending(t => t.Date)
                .ThenByDescending(t => t.PostedAt)
                .ThenByDescending(id),
            "amount-asc" => query
                .OrderBy(t => t.Amount < 0 ? -t.Amount : t.Amount)
                .ThenByDescending(t => t.Date)
                .ThenByDescending(t => t.PostedAt)
                .ThenByDescending(id),
            _ => query
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.PostedAt)
                .ThenByDescending(id)
        };
    }

    private static string EscapeCsvField(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static string EscapeCsvTextField(string value)
    {
        if (!string.IsNullOrEmpty(value) && "=+-@\t\r".Contains(value[0]))
        {
            value = $"'{value}";
        }
        return EscapeCsvField(value);
    }

    private static string FormatAmount(decimal amount)
    {
        return amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string DisplayLedgerAllocation(string ledgerCategory)
    {
        if (string.Equals(ledgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase)) return "Between accounts";
        if (ledgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase)) return "Income";
        if (ledgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
            return ledgerCategory.Substring("Transfer:".Length).Replace("->", " -> ");
        return ledgerCategory;
    }
}
