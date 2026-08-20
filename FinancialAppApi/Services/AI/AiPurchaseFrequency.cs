using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    internal sealed record AiPurchaseMatch(string Id, DateOnly Date, string Description);
    private sealed record AiPurchaseDbMatch(string Id, DateTime Date, string Description);

    internal sealed record AiPurchaseFrequencyMetric(
        string Query,
        string MatchMode,
        int TransactionCount,
        int PurchaseDayCount,
        string? FirstPurchaseDate,
        string? LastPurchaseDate,
        decimal? MedianGapDays,
        decimal PurchaseDaysPerCycle,
        int ObservedCycleCount,
        int? DaysSinceLastPurchase,
        string? TypicalCadence,
        string? SearchedFrom,
        string SearchedThrough,
        IReadOnlyList<string> SampleDescriptions,
        string Confidence);

    private async Task<AiPurchaseFrequencyMetric?> LoadPurchaseFrequencyAsync(
        AiQueryPlan queryPlan,
        TargetCycleSelection targetSelection,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        if (!queryPlan.Metrics.Contains(DerivedMetric.PurchaseCadence) ||
            string.IsNullOrWhiteSpace(queryPlan.SearchText))
        {
            return null;
        }

        var exact = await LoadPurchaseMatchesAsync(
            queryPlan.SearchText, targetSelection, cycleDay, fuzzy: false, cancellationToken);
        var mode = exact.Count > 0 ? "exact" : "none";
        var matches = exact;
        if (matches.Count == 0)
        {
            matches = await LoadPurchaseMatchesAsync(
                queryPlan.SearchText, targetSelection, cycleDay, fuzzy: true, cancellationToken);
            if (matches.Count > 0) mode = "fuzzy";
        }

        var today = _financialClock.Today;
        var scopeStart = targetSelection.AllHistory
            ? await EarliestSavedLedgerDateAsync(cancellationToken)
            : targetSelection.Cycles.Count > 0
                ? DateOnly.FromDateTime(MergeCycleRanges(targetSelection.Cycles, cycleDay).Min(range => range.Start))
                : (DateOnly?)null;
        var scopeEnd = targetSelection.AllHistory || targetSelection.Cycles.Count == 0
            ? today
            : DateOnly.FromDateTime(MergeCycleRanges(targetSelection.Cycles, cycleDay).Max(range => range.End.AddDays(-1)));

        return BuildPurchaseFrequencyMetric(
            queryPlan.SearchText,
            mode,
            matches,
            scopeStart,
            scopeEnd,
            today,
            cycleDay);
    }

    private async Task<List<AiPurchaseMatch>> LoadPurchaseMatchesAsync(
        string searchText,
        TargetCycleSelection targetSelection,
        int cycleDay,
        bool fuzzy,
        CancellationToken cancellationToken)
    {
        var ranges = targetSelection.AllHistory || targetSelection.Cycles.Count == 0
            ? new List<TransactionDateRange?> { null }
            : MergeCycleRanges(targetSelection.Cycles, cycleDay).Select(range => (TransactionDateRange?)range).ToList();
        var matches = new List<AiPurchaseMatch>();

        foreach (var range in ranges)
        {
            var baseQuery = PurchaseCandidates(range);
            if (!fuzzy)
            {
                var rows = await TransactionTextSearch
                    .ApplyExact(baseQuery, _context.Database.IsNpgsql(), searchText)
                    .Select(transaction => new AiPurchaseDbMatch(
                        transaction.Id,
                        transaction.Date,
                        transaction.Description))
                    .ToListAsync(cancellationToken);
                matches.AddRange(rows.Select(ToPurchaseMatch));
                continue;
            }

            if (_context.Database.IsNpgsql())
            {
                var rows = await TransactionTextSearch
                    .ApplyFuzzy(baseQuery, searchText)
                    .Select(transaction => new AiPurchaseDbMatch(
                        transaction.Id,
                        transaction.Date,
                        transaction.Description))
                    .ToListAsync(cancellationToken);
                matches.AddRange(rows.Select(ToPurchaseMatch));
            }
            else
            {
                var candidates = await baseQuery
                    .Select(transaction => new
                    {
                        transaction.Id,
                        transaction.Date,
                        transaction.Description,
                        transaction.Category,
                        transaction.LedgerCategory
                    })
                    .ToListAsync(cancellationToken);
                matches.AddRange(candidates
                    .Where(candidate => TransactionTextSearch.IsFuzzyMatch(
                        searchText, candidate.Description, candidate.Category, candidate.LedgerCategory))
                    .Select(candidate => new AiPurchaseMatch(
                        candidate.Id,
                        TransactionDate.ToDateOnly(candidate.Date),
                        candidate.Description)));
            }
        }

        return matches
            .GroupBy(match => match.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(match => match.Date)
            .ThenBy(match => match.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static AiPurchaseMatch ToPurchaseMatch(AiPurchaseDbMatch row) =>
        new(row.Id, TransactionDate.ToDateOnly(row.Date), row.Description);

    private IQueryable<Transaction> PurchaseCandidates(TransactionDateRange? range)
    {
        var query = _context.Transactions
            .AsNoTracking()
            .Where(transaction =>
                transaction.Amount < 0m &&
                !transaction.ExcludeFromAutocomplete &&
                transaction.Category.ToLower() != "transfer" &&
                transaction.Category.ToLower() != "adjustment" &&
                transaction.LedgerCategory.ToLower() != "discarded" &&
                !transaction.LedgerCategory.ToLower().StartsWith("transfer:") &&
                transaction.LedgerCategory.ToLower() != "accountmove");
        if (range != null)
        {
            query = query.Where(transaction =>
                transaction.Date >= range.Start && transaction.Date < range.End);
        }
        return query;
    }

    private async Task<DateOnly?> EarliestSavedLedgerDateAsync(CancellationToken cancellationToken)
    {
        var first = await _context.Transactions
            .AsNoTracking()
            .Where(transaction =>
                transaction.LedgerCategory != "Discarded" &&
                !transaction.ExcludeFromAutocomplete)
            .OrderBy(transaction => transaction.Date)
            .Select(transaction => (DateTime?)transaction.Date)
            .FirstOrDefaultAsync(cancellationToken);
        return first.HasValue ? TransactionDate.ToDateOnly(first.Value) : null;
    }

    internal static AiPurchaseFrequencyMetric BuildPurchaseFrequencyMetric(
        string query,
        string matchMode,
        IReadOnlyList<AiPurchaseMatch> matches,
        DateOnly? scopeStart,
        DateOnly scopeEnd,
        DateOnly today,
        int cycleDay)
    {
        var purchaseDays = matches.Select(match => match.Date).Distinct().Order().ToList();
        var gaps = purchaseDays.Zip(purchaseDays.Skip(1), (first, second) => second.DayNumber - first.DayNumber)
            .Order()
            .ToList();
        decimal? medianGap = gaps.Count == 0
            ? null
            : gaps.Count % 2 == 1
                ? gaps[gaps.Count / 2]
                : (gaps[gaps.Count / 2 - 1] + gaps[gaps.Count / 2]) / 2m;
        var observedCycles = scopeStart.HasValue ? CountCycles(scopeStart.Value, scopeEnd, cycleDay) : 0;
        var purchaseDaysPerCycle = observedCycles == 0
            ? 0m
            : decimal.Round(purchaseDays.Count / (decimal)observedCycles, 2, MidpointRounding.AwayFromZero);
        var last = purchaseDays.Count > 0 ? purchaseDays[^1] : (DateOnly?)null;

        return new AiPurchaseFrequencyMetric(
            query,
            matches.Count == 0 ? "none" : matchMode,
            matches.Count,
            purchaseDays.Count,
            purchaseDays.FirstOrDefault() == default ? null : purchaseDays[0].ToString("yyyy-MM-dd"),
            last?.ToString("yyyy-MM-dd"),
            medianGap,
            purchaseDaysPerCycle,
            observedCycles,
            last.HasValue ? Math.Max(0, today.DayNumber - last.Value.DayNumber) : null,
            FormatCadence(medianGap),
            scopeStart?.ToString("yyyy-MM-dd"),
            scopeEnd.ToString("yyyy-MM-dd"),
            matches.Select(match => match.Description).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList(),
            purchaseDays.Count switch { 0 => "none", 1 => "insufficient", _ => "usable" });
    }

    private static int CountCycles(DateOnly start, DateOnly end, int cycleDay)
    {
        var first = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(start, cycleDay);
        var last = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(end, cycleDay);
        var firstOrdinal = first.year * 12 + first.monthIndex - 1;
        var lastOrdinal = last.year * 12 + last.monthIndex - 1;
        return Math.Max(1, lastOrdinal - firstOrdinal + 1);
    }

    private static string? FormatCadence(decimal? medianGapDays)
    {
        if (!medianGapDays.HasValue) return null;
        if (medianGapDays.Value < 14m)
        {
            var days = Math.Max(1, decimal.Round(medianGapDays.Value, 0, MidpointRounding.AwayFromZero));
            return $"about every {days:0} {(days == 1m ? "day" : "days")}";
        }
        if (medianGapDays.Value < 60m)
        {
            var weeks = decimal.Round(medianGapDays.Value / 7m, 1, MidpointRounding.AwayFromZero);
            return $"about every {weeks:0.#} {(weeks == 1m ? "week" : "weeks")}";
        }

        var months = decimal.Round(medianGapDays.Value / 30.44m, 1, MidpointRounding.AwayFromZero);
        return $"about every {months:0.#} {(months == 1m ? "month" : "months")}";
    }
}
