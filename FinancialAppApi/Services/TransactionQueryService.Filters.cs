using System.Globalization;
using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services;

public partial class TransactionQueryService
{
    private static IQueryable<Transaction> ApplyAllFilters(
        IQueryable<Transaction> query,
        bool useIlike,
        string? search,
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
        string? wishlistFilter)
    {
        query = query.Where(t => t.LedgerCategory != "Discarded");

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
            query = TransactionTextSearch.ApplyExact(query, useIlike, search);
        }

        if (!string.IsNullOrWhiteSpace(ledgerCategory))
        {
            var buckets = ledgerCategory.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.Trim().ToLower()).ToList();

            query = query.Where(t =>
                buckets.Any(bucket =>
                    bucket == "income"
                        ? (t.LedgerCategory.ToLower() == "income" || t.LedgerCategory.ToLower().StartsWith("incomesplit:"))
                        : (t.LedgerCategory.ToLower() == bucket || t.LedgerCategory.ToLower().Contains(bucket))));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var cats = category.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim().ToLower()).ToList();
            query = query.Where(t => cats.Contains(t.Category.ToLower()));
        }

        if (!string.IsNullOrWhiteSpace(txType))
        {
            if (txType == "inflow")
                query = query.Where(t =>
                    t.Amount > 0 &&
                    t.Category.ToLower() != "transfer" &&
                    t.Category.ToLower() != "adjustment" &&
                    !t.LedgerCategory.ToLower().StartsWith("transfer:") &&
                    t.LedgerCategory.ToLower() != "discarded");
            else if (txType == "outflow")
                query = query.Where(t =>
                    t.Amount < 0 &&
                    t.Category.ToLower() != "transfer" &&
                    t.Category.ToLower() != "adjustment" &&
                    !t.LedgerCategory.ToLower().StartsWith("transfer:") &&
                    t.LedgerCategory.ToLower() != "discarded");
            else if (txType == "transfer")
                query = query.Where(t =>
                    t.Category.ToLower() == "transfer" ||
                    t.LedgerCategory.ToLower().StartsWith("transfer:"));
        }

        return query;
    }

    private static string NormalizeLinkFilter(string? filter, bool legacyOnly) =>
        filter?.Trim().ToLowerInvariant() switch
        {
            "only" => "only",
            "exclude" => "exclude",
            _ => legacyOnly ? "only" : "all"
        };

    private static IOrderedQueryable<Transaction> ApplySort(
        IQueryable<Transaction> query,
        string? sort)
    {
        return sort switch
        {
            "date-asc" => query
                .OrderBy(t => t.Date)
                .ThenBy(t => t.PostedAt)
                .ThenBy(t => t.Id),
            "amount-desc" => query
                .OrderByDescending(t => t.Amount < 0 ? -t.Amount : t.Amount)
                .ThenByDescending(t => t.Date)
                .ThenByDescending(t => t.PostedAt)
                .ThenByDescending(t => t.Id),
            "amount-asc" => query
                .OrderBy(t => t.Amount < 0 ? -t.Amount : t.Amount)
                .ThenByDescending(t => t.Date)
                .ThenByDescending(t => t.PostedAt)
                .ThenByDescending(t => t.Id),
            _ => query
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.PostedAt)
                .ThenByDescending(t => t.Id)
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
