using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Documents;

public sealed record ReliefCategoryDocumentUpdate(int Id, string? ReliefCategory);
public sealed record ReliefCategoryDocumentUpdateResult(int Id, bool Updated, string? Message = null);

/// <summary>
/// Outcome codes for a vault document metadata update.
/// </summary>
public enum DocumentVaultUpdateStatus
{
    Updated,
    NotFound,
    InvalidMetadata
}

/// <summary>
/// The result of a vault document metadata update attempt. <paramref name="Message"/> is the
/// reason an update was refused, and is written for the person who will read it on screen.
/// </summary>
public sealed record DocumentVaultUpdateResult(
    DocumentVaultUpdateStatus Status,
    VaultDocumentDto? Document = null,
    string? Message = null);

/// <summary>
/// The two metadata write paths for vault documents: one document at a time, and the bulk
/// relief-category assignment the Vault's staging bar uses. They live together because they refuse
/// edits for the same reasons, and the reasons are shown to the user verbatim — kept apart, the
/// single-document path had no wording at all for a rejection the bulk path already explained.
/// </summary>
public sealed partial class DocumentVaultService
{
    // Shared refusal wording. Each of these is already on screen somewhere else — the upload path,
    // the bulk path, or the client-side amount editor — and a second phrasing of the same refusal
    // is how the same problem starts reading as two different problems.
    private const string ReliefCategoryRequiredMessage = "A tax relief category is required.";
    private const string ReliefCategoryNotConfiguredMessage = "The category is not configured for this document's tax year.";
    private const string AmountOutOfRangeMessage = "Enter an amount of zero or more.";
    private const string DocumentNotFoundMessage = "That document is no longer in your Vault.";
    private const string UnsupportedAmountDetailsMessage = "Those document details could not be saved.";

    public async Task<DocumentVaultUpdateResult> UpdateAsync(
        int id,
        int? taxYear,
        string? transactionId,
        bool updateTransactionId,
        string? reliefCategory,
        bool updateReliefCategory,
        decimal? amount,
        bool updateAmount,
        string? amountCurrency,
        string? amountStatus,
        CancellationToken ct = default)
    {
        var doc = await _context.VaultDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        // Deliberately the same answer for "deleted" and "belongs to another account": the tenancy
        // query filter makes them indistinguishable here, and it must stay that way.
        if (doc == null) return Refused(DocumentVaultUpdateStatus.NotFound, DocumentNotFoundMessage);

        if (taxYear.HasValue && !IsAllowedTaxYear(taxYear.Value))
        {
            return Invalid(GetTaxYearValidationMessage());
        }
        reliefCategory = string.IsNullOrWhiteSpace(reliefCategory) ? null : reliefCategory.Trim();
        var targetTaxYear = taxYear ?? doc.TaxYear;
        if (updateReliefCategory && reliefCategory == null)
        {
            return Invalid(ReliefCategoryRequiredMessage);
        }
        if (updateReliefCategory && !await IsReliefCategoryConfiguredAsync(targetTaxYear, reliefCategory!, ct))
        {
            return Invalid(ReliefCategoryNotConfiguredMessage);
        }
        if (updateAmount && amount is < 0)
        {
            return Invalid(AmountOutOfRangeMessage);
        }
        // Neither of the next two is reachable from any control in the app — the UI only ever sends
        // MYR/OTHER and Confirmed/NeedsReview. A specific message here would describe a client bug.
        if (amountCurrency != null && amountCurrency is not ("MYR" or "OTHER"))
        {
            return Invalid(UnsupportedAmountDetailsMessage);
        }
        if (amountStatus != null && amountStatus is not ("Confirmed" or "NeedsReview"))
        {
            return Invalid(UnsupportedAmountDetailsMessage);
        }
        if (taxYear.HasValue && taxYear.Value != doc.TaxYear)
        {
            doc.TaxYear = taxYear.Value;
            doc.RetentionUntil = DocumentRetentionService.RetentionUntilFor(taxYear.Value);
            // StorageObjectPath is deliberately left alone; see the invariant note on CreateAsync.
        }
        if (updateTransactionId)
        {
            doc.TransactionId = string.IsNullOrWhiteSpace(transactionId) ? null : transactionId;
            // An explicit re-point (including the transaction form's Detach) is the user's final
            // word on where this document belongs, so it must not be re-linked later by a restore
            // of the transaction it happened to be attached to before.
            doc.DetachedFromTransactionId = null;
        }
        if (updateReliefCategory)
        {
            doc.ReliefCategory = reliefCategory;
        }
        if (updateAmount)
        {
            doc.Amount = amount;
            doc.AmountCurrency = amountCurrency ?? doc.AmountCurrency;
            doc.AmountStatus = amount.HasValue ? amountStatus ?? "Confirmed" : "NotFound";
            // A hand-entered figure carries no model confidence, whatever it is filed as. Keeping
            // the extractor's number here attributed a human's amount to the extractor.
            doc.AmountConfidence = null;
            doc.AmountExtractionMessage = null;
        }

        await _context.SaveChangesAsync(ct);

        return new DocumentVaultUpdateResult(DocumentVaultUpdateStatus.Updated, ToDto(doc));
    }

    /// <summary>
    /// Maps a tracked entity to the response DTO. <see cref="ListAsync"/> deliberately does not use
    /// this — its projection runs in SQL and selects only the columns it needs.
    /// </summary>
    private static VaultDocumentDto ToDto(Models.VaultDocument doc) => new(
        doc.Id,
        doc.OriginalFileName,
        doc.ContentType,
        doc.SizeBytes,
        doc.TaxYear,
        doc.ReliefCategory,
        doc.Amount,
        doc.AmountCurrency,
        doc.AmountStatus,
        doc.AmountConfidence,
        doc.AmountExtractionMessage,
        doc.TransactionId,
        doc.UploadedAt,
        doc.RetentionUntil);

    private static DocumentVaultUpdateResult Invalid(string message) =>
        Refused(DocumentVaultUpdateStatus.InvalidMetadata, message);

    private static DocumentVaultUpdateResult Refused(DocumentVaultUpdateStatus status, string message) =>
        new(status, Message: message);

    public async Task<IReadOnlyList<ReliefCategoryDocumentUpdateResult>> UpdateReliefCategoriesAsync(
        IReadOnlyCollection<ReliefCategoryDocumentUpdate> updates,
        CancellationToken ct = default)
    {
        var requested = updates
            .GroupBy(update => update.Id)
            .Select(group => group.Last())
            .ToList();

        if (requested.Count == 0) return [];

        var ids = requested
            .Where(update => update.Id > 0)
            .Select(update => update.Id)
            .ToArray();
        var documents = await _context.VaultDocuments
            .Where(document => ids.Contains(document.Id))
            .ToListAsync(ct);
        var documentsById = documents.ToDictionary(document => document.Id);
        var validCategoriesByTaxYear = new Dictionary<int, HashSet<string>>();
        var results = new List<ReliefCategoryDocumentUpdateResult>(requested.Count);
        var hasChanges = false;

        foreach (var update in requested)
        {
            if (!documentsById.TryGetValue(update.Id, out var document))
            {
                results.Add(new(update.Id, false, DocumentNotFoundMessage));
                continue;
            }

            var reliefCategory = update.ReliefCategory?.Trim();
            if (string.IsNullOrWhiteSpace(reliefCategory) || reliefCategory.Length > 80)
            {
                results.Add(new(update.Id, false, ReliefCategoryRequiredMessage));
                continue;
            }

            if (!validCategoriesByTaxYear.TryGetValue(document.TaxYear, out var validCategories))
            {
                var (rows, _) = await GetEffectiveCategoryRowsAsync(document.TaxYear, ct);
                validCategories = rows
                    .Select(row => row.CategoryId)
                    .ToHashSet(StringComparer.Ordinal);
                validCategoriesByTaxYear[document.TaxYear] = validCategories;
            }

            if (!validCategories.Contains(reliefCategory))
            {
                results.Add(new(update.Id, false, ReliefCategoryNotConfiguredMessage));
                continue;
            }

            if (!string.Equals(document.ReliefCategory, reliefCategory, StringComparison.Ordinal))
            {
                document.ReliefCategory = reliefCategory;
                hasChanges = true;
            }

            results.Add(new(update.Id, true));
        }

        if (hasChanges)
        {
            await _context.SaveChangesAsync(ct);
        }

        return results;
    }
}
