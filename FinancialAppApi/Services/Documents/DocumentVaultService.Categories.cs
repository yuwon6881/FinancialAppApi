using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace FinancialAppApi.Services.Documents;

public partial class DocumentVaultService
{
    // How far back a new upload may be filed. It is the same span records are worth keeping for, so
    // it reads from one place: a year that can still be filed is a year still worth keeping.
    private const int TaxYearLookbackYears = DocumentRetentionService.KeepYears;

    /// <summary>
    /// What a tax vault will accept: a photo of a receipt, or a PDF. `FileSignatureInspector` also
    /// recognises XML and JSON, and those are deliberately excluded here — a tax record is never one
    /// of them, and they are the only detected types a browser will both render and execute, which
    /// matters because <c>/documents/{id}/content</c> is served from the origin holding the auth
    /// cookie. Existing rows of those types stay listed and downloadable; only new uploads are refused.
    /// </summary>
    private static readonly HashSet<string> AcceptedUploadTypes = new(StringComparer.Ordinal)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/heic",
        "image/heif",
        "application/pdf",
    };

    private async Task<bool> IsReliefCategoryConfiguredAsync(
        int taxYear,
        string categoryId,
        CancellationToken ct)
    {
        var (rows, _) = await GetEffectiveCategoryRowsAsync(taxYear, ct);
        return rows.Any(row => row.CategoryId == categoryId);
    }

    private async Task<(List<TaxReliefCategoryLimit> Rows, bool IsInherited)> GetEffectiveCategoryRowsAsync(
        int taxYear,
        CancellationToken ct)
    {
        var current = await _context.TaxReliefCategoryLimits
            .AsNoTracking()
            .Where(row => row.TaxYear == taxYear)
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
        if (current.Count > 0)
        {
            return (current, false);
        }

        var previousYear = await _context.TaxReliefCategoryLimits
            .AsNoTracking()
            .Where(row => row.TaxYear < taxYear)
            .OrderByDescending(row => row.TaxYear)
            .Select(row => (int?)row.TaxYear)
            .FirstOrDefaultAsync(ct);
        if (!previousYear.HasValue)
        {
            return ([], true);
        }

        var inherited = await _context.TaxReliefCategoryLimits
            .AsNoTracking()
            .Where(row => row.TaxYear == previousYear.Value)
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
        return (inherited, true);
    }

    private async Task<List<TaxReliefCategoryLimit>> EnsureExplicitConfigurationAsync(
        int taxYear,
        CancellationToken ct)
    {
        var current = await _context.TaxReliefCategoryLimits
            .Where(row => row.TaxYear == taxYear)
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
        if (current.Count > 0)
        {
            return current;
        }

        var (inherited, _) = await GetEffectiveCategoryRowsAsync(taxYear, ct);
        foreach (var row in inherited)
        {
            _context.TaxReliefCategoryLimits.Add(new TaxReliefCategoryLimit
            {
                CategoryId = row.CategoryId,
                Name = row.Name,
                Limit = row.Limit,
                TaxYear = taxYear
            });
        }

        if (inherited.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
        }

        return await _context.TaxReliefCategoryLimits
            .Where(row => row.TaxYear == taxYear)
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
    }

    private static TaxReliefCategoryDefinition ToDefinition(
        TaxReliefCategoryLimit row,
        bool isInherited = false) =>
        new(row.CategoryId, row.Name, row.Limit, isInherited);

    private static bool TryNormalizeCategoryInput(
        string? name,
        decimal limit,
        out string normalizedName)
    {
        normalizedName = name?.Trim() ?? string.Empty;
        return normalizedName.Length is > 0 and <= 120 &&
               limit is >= 0 and <= 9_999_999_999_999_999.99m;
    }

    private static string CreateCategoryId(
        string name,
        IReadOnlyCollection<TaxReliefCategoryLimit> existingRows)
    {
        var builder = new StringBuilder();
        foreach (var character in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var baseId = builder.ToString().Trim('-');
        if (baseId.Length == 0) baseId = "category";
        if (baseId.Length > 60) baseId = baseId[..60].TrimEnd('-');

        var candidate = baseId;
        var suffix = 2;
        while (existingRows.Any(row => string.Equals(row.CategoryId, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            var suffixText = $"-{suffix++}";
            candidate = baseId[..Math.Min(baseId.Length, 80 - suffixText.Length)] + suffixText;
        }

        return candidate;
    }

    private bool IsAllowedTaxYear(int taxYear)
    {
        var currentYear = _financialClock.Today.Year;
        return taxYear >= currentYear - TaxYearLookbackYears && taxYear <= currentYear;
    }

    private string GetTaxYearValidationMessage()
    {
        var currentYear = _financialClock.Today.Year;
        return $"Tax year must be between {currentYear - TaxYearLookbackYears} and {currentYear}.";
    }
}
