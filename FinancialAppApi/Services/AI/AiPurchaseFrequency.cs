using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    internal sealed record AiPurchaseMatch(string Id, DateOnly Date, string Description);
    private sealed record AiPurchaseDbMatch(string Id, DateTime Date, string Description);

    // The ladder a cadence search walks. Exact is the literal substring; compact retries the same
    // term with separators removed on both sides ("hair cut" against a saved "Haircut"); fuzzy is
    // the trigram pass, tried last because it can pair genuinely different words.
    private enum PurchaseMatchMode { Exact, Compact, Fuzzy }

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
        string? NextExpectedDate,
        int? DaysUntilNextExpected,
        bool NextExpectedIsOverdue,
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

        var mode = "none";
        var matches = new List<AiPurchaseMatch>();
        // Each rung is reached only when the stricter one above it found nothing, so an exact hit
        // is never diluted by a looser reading of the same term.
        var candidates = queryPlan.SearchMode == "whole-word"
            ? new[] { PurchaseMatchMode.Exact }
            : new[] { PurchaseMatchMode.Exact, PurchaseMatchMode.Compact, PurchaseMatchMode.Fuzzy };
        foreach (var candidate in candidates)
        {
            if (candidate == PurchaseMatchMode.Compact && !TransactionTextSearch.CanApplyCompact(queryPlan.SearchText))
            {
                continue;
            }
            matches = await LoadPurchaseMatchesAsync(
                queryPlan.SearchText, targetSelection, cycleDay, candidate, cancellationToken, queryPlan.SearchMode);
            if (matches.Count > 0)
            {
                mode = candidate switch
                {
                    PurchaseMatchMode.Exact => "exact",
                    PurchaseMatchMode.Compact => "spacing",
                    _ => "fuzzy"
                };
                break;
            }
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
        PurchaseMatchMode matchMode,
        CancellationToken cancellationToken,
        string searchMode)
    {
        var ranges = targetSelection.AllHistory || targetSelection.Cycles.Count == 0
            ? new List<TransactionDateRange?> { null }
            : MergeCycleRanges(targetSelection.Cycles, cycleDay).Select(range => (TransactionDateRange?)range).ToList();
        var matches = new List<AiPurchaseMatch>();

        foreach (var range in ranges)
        {
            var baseQuery = PurchaseCandidates(range);
            if (matchMode != PurchaseMatchMode.Fuzzy)
            {
                var filtered = matchMode == PurchaseMatchMode.Exact
                    ? TransactionTextSearch.Apply(baseQuery, _context.Database.IsNpgsql(), searchText, searchMode)
                    : TransactionTextSearch.ApplyCompact(baseQuery, searchText);
                var rows = await filtered
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
        // "When is the next one due" is the other half of a cadence question, so the projection is
        // derived here: the prompt forbids the model from doing cadence arithmetic itself, which
        // left "estimate the next one" unanswerable even when the history was right there. A
        // projected date already in the past is reported as overdue rather than rolled silently
        // forward -- being late is the honest answer in that case.
        var nextExpected = last.HasValue && medianGap.HasValue
            ? last.Value.AddDays((int)decimal.Round(medianGap.Value, 0, MidpointRounding.AwayFromZero))
            : (DateOnly?)null;

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
            nextExpected?.ToString("yyyy-MM-dd"),
            nextExpected.HasValue ? nextExpected.Value.DayNumber - today.DayNumber : null,
            nextExpected.HasValue && nextExpected.Value < today,
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
