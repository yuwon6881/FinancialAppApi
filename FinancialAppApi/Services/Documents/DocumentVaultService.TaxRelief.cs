using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Documents;

public partial class DocumentVaultService
{
    public async Task<TaxYearReliefSummary?> GetTaxYearSummaryAsync(int taxYear, CancellationToken ct = default)
    {
        if (!IsAllowedTaxYear(taxYear)) return null;

        // Aggregated per category in SQL rather than by pulling every document for the year back and
        // summing in memory. Only Confirmed MYR amounts count towards a relief limit, which is why
        // the currency and status appear inside each sum rather than as a filter over the whole set.
        var perCategory = await _context.VaultDocuments
            .AsNoTracking()
            .Where(document => document.TaxYear == taxYear)
            .GroupBy(document => document.ReliefCategory)
            .Select(group => new
            {
                ReliefCategory = group.Key,
                DocumentCount = group.Count(),
                Confirmed = group.Sum(document =>
                    document.AmountStatus == "Confirmed" && document.AmountCurrency == "MYR"
                        ? document.Amount ?? 0
                        : 0),
                Pending = group.Sum(document =>
                    document.AmountStatus == "NeedsReview" && document.AmountCurrency == "MYR"
                        ? document.Amount ?? 0
                        : 0),
                PendingReviewCount = group.Count(document => document.AmountStatus == "NeedsReview"),
            })
            .ToListAsync(ct);

        var definitions = await GetReliefCategoriesAsync(taxYear, ct);
        if (perCategory.Count == 0 && definitions.Count == 0) return null;

        var totalsByCategory = perCategory
            .Where(entry => entry.ReliefCategory != null)
            .ToDictionary(entry => entry.ReliefCategory!, StringComparer.Ordinal);

        var summaries = definitions.Select(definition =>
        {
            totalsByCategory.TryGetValue(definition.Id, out var totals);
            return new TaxReliefCategorySummary(
                definition.Id,
                definition.Name,
                definition.Limit,
                totals?.Confirmed ?? 0,
                totals?.Pending ?? 0,
                totals?.DocumentCount ?? 0,
                totals?.PendingReviewCount ?? 0);
        }).ToList();

        return new TaxYearReliefSummary(
            taxYear,
            summaries.Sum(summary => summary.ConfirmedAmount),
            summaries.Sum(summary => summary.PendingReviewAmount),
            // Every document for the year, including any whose category is unset or no longer
            // configured — those are exactly the ones needing repair, so they must still be counted.
            perCategory.Sum(entry => entry.DocumentCount),
            summaries);
    }

    public async Task<IReadOnlyList<TaxReliefCategoryDefinition>> GetReliefCategoriesAsync(
        int taxYear,
        CancellationToken ct = default)
    {
        if (!IsAllowedTaxYear(taxYear)) return [];

        var (rows, isInherited) = await GetEffectiveCategoryRowsAsync(taxYear, ct);
        return rows
            .Select(row => ToDefinition(row, isInherited))
            .ToList();
    }

    public bool IsTaxYearAllowed(int taxYear) => IsAllowedTaxYear(taxYear);

    /// <summary>The wording every caller uses when a tax year is out of range, so they cannot differ.</summary>
    public string TaxYearValidationMessage => GetTaxYearValidationMessage();

    public async Task<TaxReliefCategoryMutationResult> AddReliefCategoryAsync(
        int taxYear,
        string? name,
        decimal limit,
        CancellationToken ct = default)
    {
        if (!TryNormalizeCategoryInput(name, limit, out var normalizedName))
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Invalid);
        }
        if (!IsAllowedTaxYear(taxYear))
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Invalid);
        }

        var rows = await EnsureExplicitConfigurationAsync(taxYear, ct);
        if (rows.Any(row => string.Equals(row.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Duplicate);
        }

        var category = new TaxReliefCategoryLimit
        {
            CategoryId = CreateCategoryId(normalizedName, rows),
            Name = normalizedName,
            Limit = limit,
            TaxYear = taxYear
        };
        _context.TaxReliefCategoryLimits.Add(category);
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The duplicate check above reads rows loaded before this insert, so two adds of the same
            // name race to the same generated CategoryId and one of them loses on the unique index.
            // That is the same conflict the check reports, and it must read the same way rather than
            // escaping as a 500.
            _context.Entry(category).State = EntityState.Detached;
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Duplicate);
        }

        return new TaxReliefCategoryMutationResult(
            TaxReliefCategoryMutationStatus.Saved,
            ToDefinition(category));
    }

    public async Task<TaxReliefCategoryMutationResult> UpdateReliefCategoryAsync(
        int taxYear,
        string categoryId,
        string? name,
        decimal limit,
        CancellationToken ct = default)
    {
        if (!TryNormalizeCategoryInput(name, limit, out var normalizedName) ||
            string.IsNullOrWhiteSpace(categoryId) || categoryId.Length > 80 ||
            !IsAllowedTaxYear(taxYear))
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Invalid);
        }

        var rows = await EnsureExplicitConfigurationAsync(taxYear, ct);
        var category = rows.FirstOrDefault(row => row.CategoryId == categoryId);
        if (category == null)
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.NotFound);
        }
        if (rows.Any(row => row.Id != category.Id &&
                           string.Equals(row.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Duplicate);
        }

        category.Name = normalizedName;
        category.Limit = limit;
        await _context.SaveChangesAsync(ct);

        return new TaxReliefCategoryMutationResult(
            TaxReliefCategoryMutationStatus.Saved,
            ToDefinition(category));
    }

    /// <summary>
    /// Removes a relief category from one tax year. Documents carry a required relief category,
    /// so a category that is still referenced by a document in that year is refused rather than
    /// leaving those documents pointing at a category that no longer exists.
    /// </summary>
    public async Task<TaxReliefCategoryMutationResult> DeleteReliefCategoryAsync(
        int taxYear,
        string categoryId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(categoryId) || categoryId.Length > 80 || !IsAllowedTaxYear(taxYear))
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Invalid);
        }

        var rows = await EnsureExplicitConfigurationAsync(taxYear, ct);
        var category = rows.FirstOrDefault(row => row.CategoryId == categoryId);
        if (category == null)
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.NotFound);
        }

        var isInUse = await _context.VaultDocuments
            .AnyAsync(document => document.TaxYear == taxYear && document.ReliefCategory == categoryId, ct);
        if (isInUse)
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.InUse);
        }

        _context.TaxReliefCategoryLimits.Remove(category);
        await _context.SaveChangesAsync(ct);

        return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Saved);
    }

    public Task<bool> HasDocumentsForExportAsync(int? taxYear, CancellationToken ct = default)
    {
        return ExportQuery(taxYear, null).AnyAsync(ct);
    }

    public async Task WriteZipAsync(int? taxYear, Stream output, CancellationToken ct = default)
    {
        await WriteZipAsync(ExportQuery(taxYear, null), output, ct);
    }

    public async Task<bool> HasAllDocumentsForExportAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        var distinctIds = ids.Distinct().ToArray();
        return distinctIds.Length > 0 && await ExportQuery(null, distinctIds).CountAsync(ct) == distinctIds.Length;
    }
}
