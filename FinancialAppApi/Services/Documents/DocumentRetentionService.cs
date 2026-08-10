using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Documents;

/// <summary>
/// One tax year's stored records, measured against the date they stop being worth keeping.
/// <paramref name="DaysUntilKeepUntil"/> is negative once that date has passed, which is the only
/// thing that separates a record the user may clear out now from one they should keep a while yet.
/// </summary>
public sealed record RetentionTaxYearSummary(
    int TaxYear,
    int DocumentCount,
    long TotalBytes,
    DateOnly KeepUntil,
    int DaysUntilKeepUntil);

/// <summary>
/// Everything the retention notice needs to write itself. <paramref name="NoticeWindowDays"/> and
/// <paramref name="KeepYears"/> are echoed for the same reason <see cref="DocumentVaultUsage"/>
/// echoes its quota: the client must never hold a second copy of a number the server decides.
/// </summary>
public sealed record DocumentRetentionReview(
    IReadOnlyList<RetentionTaxYearSummary> TaxYears,
    int NoticeWindowDays,
    int KeepYears);

/// <summary>
/// How long tax records are worth keeping, and which years are near or past that line.
/// <para>
/// Nothing here deletes anything, and nothing anywhere else does either — there is no purge job for
/// vault documents by design, because the app promises on screen that tax evidence is only ever
/// removed by the person who owns it. This service exists purely to say what could be cleared out.
/// </para>
/// </summary>
public sealed class DocumentRetentionService(AppDbContext context, FinancialClock? financialClock = null)
{
    /// <summary>Years a tax record is worth keeping after the end of its tax year.</summary>
    public const int KeepYears = 7;

    // Every keep-until date is a 31 December, so 180 days surfaces the notice in early July of the
    // final year: the same calendar year the records stop being needed and ahead of the next filing
    // season, without the notice living on screen for twelve months and training the eye to skip it.
    private const int RetentionNoticeWindowDays = 180;

    private readonly FinancialClock _financialClock = financialClock ?? FinancialClock.Utc;

    /// <summary>The date a record filed under <paramref name="taxYear"/> stops being worth keeping.</summary>
    public static DateOnly RetentionUntilFor(int taxYear) => new DateOnly(taxYear, 12, 31).AddYears(KeepYears);

    public async Task<DocumentRetentionReview> GetReviewAsync(CancellationToken ct = default)
    {
        var today = _financialClock.Today;
        var cutoff = today.AddDays(RetentionNoticeWindowDays);

        // Grouped in SQL rather than by pulling every matching row back and grouping in memory,
        // following the same shape as GetUsageAsync. Grouping by TaxYear alone and taking the
        // earliest keep-until is deliberate: a year holding two different retention dates used to
        // produce two summaries for the same year, which the notice rendered as "2018 (2), 2018 (1)".
        var groups = await context.VaultDocuments
            .AsNoTracking()
            .Where(document => document.RetentionUntil <= cutoff)
            .GroupBy(document => document.TaxYear)
            .Select(group => new
            {
                TaxYear = group.Key,
                DocumentCount = group.Count(),
                TotalBytes = group.Sum(document => document.SizeBytes),
                KeepUntil = group.Min(document => document.RetentionUntil),
            })
            .ToListAsync(ct);

        // DateOnly.DayNumber has no SQL translation, and this is at most a handful of rows, so the
        // day count and the ordering are done here rather than pushed into the projection.
        var taxYears = groups
            .OrderBy(group => group.KeepUntil)
            .ThenBy(group => group.TaxYear)
            .Select(group => new RetentionTaxYearSummary(
                group.TaxYear,
                group.DocumentCount,
                group.TotalBytes,
                group.KeepUntil,
                group.KeepUntil.DayNumber - today.DayNumber))
            .ToList();

        return new DocumentRetentionReview(taxYears, RetentionNoticeWindowDays, KeepYears);
    }
}
