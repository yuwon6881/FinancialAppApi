using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class DocumentVaultServiceTests
{
    [Fact]
    public async Task CreateAsync_RejectsInvalidMagicBytes()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        AddTestCategory(context);
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);

        var data = new byte[] { 0x00, 0x01, 0x02 };

        var result = await service.CreateAsync("file.txt", data, 2026, null, null, "test-category");

        Assert.Equal(DocumentVaultCreateStatus.UnsupportedType, result.Status);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><receipt/>")]
    [InlineData("{\"total\": 12.30}")]
    public async Task CreateAsync_RefusesMarkupAndDataFilesEvenThoughTheirTypeIsRecognised(string content)
    {
        // These are the only detected types a browser will both render and execute, and the content
        // endpoint serves from the origin holding the auth cookie. A tax record is never one of them.
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        AddTestCategory(context);
        await context.SaveChangesAsync();

        var service = NewService(context, new FakeDocumentVaultStore());

        var result = await service.CreateAsync("receipt.xml", System.Text.Encoding.UTF8.GetBytes(content), 2026, null, null, "test-category");

        Assert.Equal(DocumentVaultCreateStatus.UnsupportedType, result.Status);
    }

    [Fact]
    public async Task CreateAsync_RequiresTaxReliefCategory()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);
        var data = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };

        var result = await service.CreateAsync("file.pdf", data, 2026, null, null);

        Assert.Equal(DocumentVaultCreateStatus.InvalidMetadata, result.Status);
        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task CreateAsync_RejectsLargeFiles()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        AddTestCategory(context);
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store, maxBytes: 10);

        var data = new byte[11];
        data[0] = 0x25; data[1] = 0x50; data[2] = 0x44; data[3] = 0x46; data[4] = 0x2D;

        var result = await service.CreateAsync("file.pdf", data, 2026, null, null, "test-category");

        Assert.Equal(DocumentVaultCreateStatus.FileTooLarge, result.Status);
    }

    [Fact]
    public async Task CreateAsync_RejectsExpiredTaxYearForNewUploads()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        AddTestCategory(context, 2018);
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);
        var data = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };

        var result = await service.CreateAsync("file.pdf", data, 2018, null, null, "test-category");

        Assert.Equal(DocumentVaultCreateStatus.InvalidMetadata, result.Status);
        Assert.Contains("Tax year must be between", result.Message);
        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task CreateAsync_AcceptsTheSevenYearLookbackBoundary()
    {
        var currentYear = FinancialClock.Utc.Today.Year;
        var oldestAllowedYear = currentYear - 7;
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        AddTestCategory(context, oldestAllowedYear);
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);
        var data = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };

        var result = await service.CreateAsync("file.pdf", data, oldestAllowedYear, null, null, "test-category");

        Assert.Equal(DocumentVaultCreateStatus.Created, result.Status);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task ExistingExpiredDocumentsRemainReadable()
    {
        // Nothing is ever purged, so a record past its keep-until date must still list and open.
        // That it is also *reported* for review is DocumentRetentionServiceTests' subject.
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.Add(VaultDocumentForYear(2018, 1));
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);

        var listed = await service.ListAsync(2018, null, null, "uploaded-desc", 0, 10);

        Assert.Single(listed.Items);
        Assert.Equal(2018, listed.Items[0].TaxYear);
    }

    [Fact]
    public async Task ListAsync_FiltersByMultipleReliefCategoriesAsOr()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        var lifestyle = VaultDocumentForYear(2026, 1);
        lifestyle.ReliefCategory = "lifestyle";
        var education = VaultDocumentForYear(2026, 2);
        education.ReliefCategory = "education";
        var medical = VaultDocumentForYear(2026, 3);
        medical.ReliefCategory = "medical";
        context.VaultDocuments.AddRange(lifestyle, education, medical);
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);

        var listed = await service.ListAsync(2026, null, "lifestyle,education", "uploaded-desc", 0, 10);

        Assert.Equal(2, listed.Items.Count);
        Assert.DoesNotContain(listed.Items, item => item.ReliefCategory == "medical");
    }

    [Fact]
    public async Task GetAvailableTaxYearsAsync_ReturnsOnlyYearsWithDocuments()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.AddRange(
            VaultDocumentForYear(2025, 1),
            VaultDocumentForYear(2026, 2),
            VaultDocumentForYear(2025, 3));
        await context.SaveChangesAsync();

        var service = NewService(context, new FakeDocumentVaultStore());

        var years = await service.GetAvailableTaxYearsAsync();
        Assert.Equal([2026, 2025], years);
        Assert.DoesNotContain(2000, years);
    }

    [Fact]
    public async Task GetTaxYearSummaryAsync_CountsOnlyConfirmedMyrTowardLimit()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        var confirmed = VaultDocumentForYear(2025, 1);
        confirmed.ReliefCategory = "lifestyle";
        confirmed.Amount = 2000;
        confirmed.AmountCurrency = "MYR";
        confirmed.AmountStatus = "Confirmed";
        var pending = VaultDocumentForYear(2025, 2);
        pending.ReliefCategory = "lifestyle";
        pending.Amount = 800;
        pending.AmountCurrency = "MYR";
        pending.AmountStatus = "NeedsReview";
        context.VaultDocuments.AddRange(confirmed, pending);
        context.TaxReliefCategoryLimits.Add(new TaxReliefCategoryLimit
        {
            UserId = "test-user",
            CategoryId = "lifestyle",
            Name = "Lifestyle",
            Limit = 2500m,
            TaxYear = 2025
        });
        await context.SaveChangesAsync();

        var summary = await NewService(context, new FakeDocumentVaultStore()).GetTaxYearSummaryAsync(2025);

        Assert.NotNull(summary);
        var lifestyle = Assert.Single(summary.Categories);
        Assert.Equal(2000, lifestyle.ConfirmedAmount);
        Assert.Equal(800, lifestyle.PendingReviewAmount);
        Assert.Equal(2500, lifestyle.Limit);
    }

    [Fact]
    public async Task ReliefCategories_InheritUntilAYearIsEditedWithoutChangingThePriorYear()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.TaxReliefCategoryLimits.Add(new TaxReliefCategoryLimit
        {
            UserId = "test-user",
            CategoryId = "lifestyle",
            Name = "Lifestyle",
            Limit = 2500m,
            TaxYear = 2025
        });
        await context.SaveChangesAsync();

        var service = NewService(context, new FakeDocumentVaultStore());
        var inherited = await service.GetReliefCategoriesAsync(2026);
        var inheritedLifestyle = Assert.Single(inherited);
        Assert.True(inheritedLifestyle.IsInherited);
        Assert.Equal(2500m, inheritedLifestyle.Limit);

        var update = await service.UpdateReliefCategoryAsync(
            2026, "lifestyle", "Lifestyle", 3000m, CancellationToken.None);

        Assert.Equal(TaxReliefCategoryMutationStatus.Saved, update.Status);
        Assert.False(Assert.Single(await service.GetReliefCategoriesAsync(2026)).IsInherited);
        Assert.Equal(3000m, Assert.Single(await service.GetReliefCategoriesAsync(2026)).Limit);
        Assert.Equal(2500m, Assert.Single(await service.GetReliefCategoriesAsync(2025)).Limit);

        var add = await service.AddReliefCategoryAsync(2026, "Sports", 1000m);
        Assert.Equal(TaxReliefCategoryMutationStatus.Saved, add.Status);
        Assert.Single(await service.GetReliefCategoriesAsync(2025));
        Assert.Collection(await service.GetReliefCategoriesAsync(2026), _ => { }, _ => { });
    }

    [Fact]
    public async Task DeleteReliefCategoryAsync_RemovesUnusedCategoryButRefusesOneInUse()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        var filed = VaultDocumentForYear(2026, 1);
        filed.ReliefCategory = "lifestyle";
        context.VaultDocuments.Add(filed);
        context.TaxReliefCategoryLimits.AddRange(
            new TaxReliefCategoryLimit { UserId = "test-user", CategoryId = "lifestyle", Name = "Lifestyle", Limit = 2500m, TaxYear = 2026 },
            new TaxReliefCategoryLimit { UserId = "test-user", CategoryId = "sports", Name = "Sports", Limit = 1000m, TaxYear = 2026 });
        await context.SaveChangesAsync();

        var service = NewService(context, new FakeDocumentVaultStore());

        var inUse = await service.DeleteReliefCategoryAsync(2026, "lifestyle");
        Assert.Equal(TaxReliefCategoryMutationStatus.InUse, inUse.Status);

        var deleted = await service.DeleteReliefCategoryAsync(2026, "sports");
        Assert.Equal(TaxReliefCategoryMutationStatus.Saved, deleted.Status);
        Assert.Equal("lifestyle", Assert.Single(await service.GetReliefCategoriesAsync(2026)).Id);

        var missing = await service.DeleteReliefCategoryAsync(2026, "sports");
        Assert.Equal(TaxReliefCategoryMutationStatus.NotFound, missing.Status);
    }

    [Fact]
    public async Task CreateAsync_RejectsOverQuota()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        AddTestCategory(context);
        
        context.VaultDocuments.Add(new VaultDocument 
        { 
            UserId = "test-user",
            StorageObjectPath = "test-user/2026/1.pdf",
            OriginalFileName = "1.pdf",
            ContentType = "application/pdf",
            SizeBytes = 90,
            Sha256 = "hash",
            TaxYear = 2026,
            UploadedAt = DateTime.UtcNow,
            RetentionUntil = new DateOnly(2033, 12, 31)
        });
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store, maxTotalBytes: 100);

        var data = new byte[15];
        data[0] = 0x25; data[1] = 0x50; data[2] = 0x44; data[3] = 0x46; data[4] = 0x2D;

        var result = await service.CreateAsync("file.pdf", data, 2026, null, null, "test-category");

        Assert.Equal(DocumentVaultCreateStatus.QuotaExceeded, result.Status);
    }

    [Fact]
    public async Task CreateAsync_AcceptsPdf()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        AddTestCategory(context);
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);

        var data = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 1, 2, 3 };

        var result = await service.CreateAsync("file.pdf", data, 2026, null, null, "test-category");

        Assert.Equal(DocumentVaultCreateStatus.Created, result.Status);
        Assert.NotNull(result.DocumentId);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task CreateAsync_AcceptsJpeg()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        AddTestCategory(context);
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);

        var data = new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 };

        var result = await service.CreateAsync("file.jpg", data, 2026, null, null, "test-category");

        Assert.Equal(DocumentVaultCreateStatus.Created, result.Status);
        Assert.NotNull(result.DocumentId);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task CreateAsync_RollsBackStoreOnDbFailure()
    {
        var interceptor = new FailVaultDocumentSaveInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .AddInterceptors(interceptor)
            .Options;
        await using var context = new AppDbContext(options);
        context.SetCurrentUser("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        AddTestCategory(context);
        await context.SaveChangesAsync();
        interceptor.FailVaultDocumentSaves = true;

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);

        var data = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("file.pdf", data, 2026, null, null, "test-category"));

        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task DeleteAsync_KeepsRowIfStoreDeleteFails()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        
        var doc = new VaultDocument 
        { 
            Id = 1,
            UserId = "test-user",
            StorageObjectPath = "test-user/2026/1.pdf",
            OriginalFileName = "1.pdf",
            ContentType = "application/pdf",
            SizeBytes = 90,
            Sha256 = "hash",
            TaxYear = 2026,
            UploadedAt = DateTime.UtcNow,
            RetentionUntil = new DateOnly(2033, 12, 31)
        };
        context.VaultDocuments.Add(doc);
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore { FailDeletes = true };
        var service = NewService(context, store);

        var result = await service.DeleteAsync(1);

        Assert.Equal(DocumentDeleteOutcome.StorageFailed, result);
        Assert.NotNull(await context.VaultDocuments.FirstOrDefaultAsync(d => d.Id == 1));
    }

    [Fact]
    public async Task DeleteAsync_SeparatesAnAlreadyGoneDocumentFromOneItJustRemoved()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.Add(VaultDocumentForYear(2026, 1));
        await context.SaveChangesAsync();

        var service = NewService(context, new FakeDocumentVaultStore());

        Assert.Equal(DocumentDeleteOutcome.Deleted, await service.DeleteAsync(1));
        // Deleting again still succeeds — a replayed delete must not fail — but it is reported as a
        // different outcome, so a bulk result cannot count rows nobody actually removed.
        Assert.Equal(DocumentDeleteOutcome.AlreadyGone, await service.DeleteAsync(1));
        Assert.Equal(DocumentDeleteOutcome.AlreadyGone, await service.DeleteAsync(999));
    }

    [Fact]
    public async Task DeleteManyAsync_PreservesOrderAndRowsWhoseStorageDeleteFails()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.AddRange(VaultDocumentForYear(2026, 1), VaultDocumentForYear(2026, 2));
        await context.SaveChangesAsync();
        var store = new FakeDocumentVaultStore();
        store.FailedDeletePaths.Add("test-user/2026/2.pdf");

        var results = await NewService(context, store).DeleteManyAsync([999, 2, 1]);

        Assert.Equal([999, 2, 1], results.Select(result => result.Id));
        Assert.Equal(DocumentDeleteOutcome.AlreadyGone, results[0].Outcome);
        Assert.Equal(DocumentDeleteOutcome.StorageFailed, results[1].Outcome);
        Assert.Equal(DocumentDeleteOutcome.Deleted, results[2].Outcome);
        Assert.NotNull(await context.VaultDocuments.FindAsync(2));
        Assert.Null(await context.VaultDocuments.FindAsync(1));
        Assert.Equal(2, store.DeleteCalls);
        Assert.InRange(store.MaxConcurrentDeletes, 1, 4);
    }

    [Fact]
    public async Task WriteZipAsync_FailsWhenAStoredObjectIsMissing()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.Add(VaultDocumentForYear(2026, 1));
        await context.SaveChangesAsync();

        var service = NewService(context, new FakeDocumentVaultStore());

        await Assert.ThrowsAsync<DocumentVaultStoreException>(() =>
            service.WriteZipAsync(new[] { 1 }, new MemoryStream()));
    }

    [Fact]
    public async Task UpdateAsync_SaysWhyAMetadataEditWasRefused()
    {
        // Every one of these used to return null, which the controller reported as "not found" — so a
        // refused category or tax year told the user their document had vanished.
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        AddTestCategory(context);
        context.VaultDocuments.Add(VaultDocumentForYear(DateTime.UtcNow.Year, 1));
        await context.SaveChangesAsync();

        var service = NewService(context, new FakeDocumentVaultStore());

        var missing = await service.UpdateAsync(999, null, null, false, null, false, null, false, null, null);
        Assert.Equal(DocumentVaultUpdateStatus.NotFound, missing.Status);
        Assert.False(string.IsNullOrWhiteSpace(missing.Message));

        var badYear = await service.UpdateAsync(1, 1990, null, false, null, false, null, false, null, null);
        Assert.Equal(DocumentVaultUpdateStatus.InvalidMetadata, badYear.Status);
        Assert.Contains("Tax year must be between", badYear.Message);

        var blankCategory = await service.UpdateAsync(1, null, null, false, "  ", true, null, false, null, null);
        Assert.Equal(DocumentVaultUpdateStatus.InvalidMetadata, blankCategory.Status);
        Assert.Contains("required", blankCategory.Message);

        var unknownCategory = await service.UpdateAsync(1, null, null, false, "not-configured", true, null, false, null, null);
        Assert.Equal(DocumentVaultUpdateStatus.InvalidMetadata, unknownCategory.Status);
        Assert.Contains("not configured", unknownCategory.Message);

        var negativeAmount = await service.UpdateAsync(1, null, null, false, null, false, -1m, true, null, null);
        Assert.Equal(DocumentVaultUpdateStatus.InvalidMetadata, negativeAmount.Status);
        Assert.Contains("zero or more", negativeAmount.Message);
    }

    [Fact]
    public async Task UpdateAsync_DropsExtractorConfidenceWhenAPersonEntersTheAmount()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        var document = VaultDocumentForYear(DateTime.UtcNow.Year, 1);
        document.Amount = 12m;
        document.AmountConfidence = 0.42m;
        document.AmountStatus = "NeedsReview";
        context.VaultDocuments.Add(document);
        await context.SaveChangesAsync();

        var service = NewService(context, new FakeDocumentVaultStore());

        var result = await service.UpdateAsync(1, null, null, false, null, false, 99m, true, "MYR", "NeedsReview");

        Assert.Equal(DocumentVaultUpdateStatus.Updated, result.Status);
        Assert.Equal(99m, result.Document!.Amount);
        // A hand-typed figure has no model confidence, whatever status it is filed under.
        Assert.Null(result.Document.AmountConfidence);
    }

    [Fact]
    public void GetConstraints_ReturnsConfiguredLimitsAndAcceptedUploadTypes()
    {
        using var context = TestHelpers.NewInMemoryContext("test-user");
        var service = NewService(context, new FakeDocumentVaultStore(), maxBytes: 20 * 1024 * 1024, maxTotalBytes: 500 * 1024 * 1024);

        var constraints = service.GetConstraints();

        Assert.Equal(20 * 1024 * 1024, constraints.MaxDocumentBytes);
        Assert.Equal(500 * 1024 * 1024, constraints.MaxTotalBytesPerUser);
        Assert.Contains("application/pdf", constraints.AcceptedUploadTypes);
        Assert.Contains("image/jpeg", constraints.AcceptedUploadTypes);
        Assert.DoesNotContain("application/json", constraints.AcceptedUploadTypes);
        Assert.DoesNotContain("application/xml", constraints.AcceptedUploadTypes);
    }

    [Fact]
    public async Task GetTaxYearSummaryAsync_ReportsOtherCurrencyCount_And_ExcludesNonMyrFromPendingReviewCount()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        AddTestCategory(context, 2026);

        var doc1 = VaultDocumentForYear(2026, 1);
        doc1.ReliefCategory = "test-category";
        doc1.Amount = 100m;
        doc1.AmountCurrency = "MYR";
        doc1.AmountStatus = "Confirmed";

        var doc2 = VaultDocumentForYear(2026, 2);
        doc2.ReliefCategory = "test-category";
        doc2.Amount = 50m;
        doc2.AmountCurrency = "MYR";
        doc2.AmountStatus = "NeedsReview";

        var doc3 = VaultDocumentForYear(2026, 3);
        doc3.ReliefCategory = "test-category";
        doc3.Amount = 200m;
        doc3.AmountCurrency = "OTHER";
        doc3.AmountStatus = "NeedsReview";

        var doc4 = VaultDocumentForYear(2026, 4);
        doc4.ReliefCategory = "test-category";
        doc4.Amount = 300m;
        doc4.AmountCurrency = "OTHER";
        doc4.AmountStatus = "Confirmed";

        context.VaultDocuments.AddRange(doc1, doc2, doc3, doc4);
        await context.SaveChangesAsync();

        var service = NewService(context, new FakeDocumentVaultStore());
        var summary = await service.GetTaxYearSummaryAsync(2026);

        Assert.NotNull(summary);
        Assert.Equal(4, summary.DocumentCount);
        Assert.Equal(100m, summary.ConfirmedAmount);
        Assert.Equal(50m, summary.PendingReviewAmount);

        var category = Assert.Single(summary.Categories);
        Assert.Equal("test-category", category.Id);
        Assert.Equal(4, category.DocumentCount);
        Assert.Equal(100m, category.ConfirmedAmount);
        Assert.Equal(50m, category.PendingReviewAmount);
        Assert.Equal(1, category.PendingReviewCount);
        Assert.Equal(2, category.OtherCurrencyDocumentCount);
    }

    // ---------- Bulk transaction relinking ----------
    // Post-sync reconciliation detaches every document a synced transaction gave up, and did it one
    // PATCH at a time; a batch of drafts pushed together turned that into a burst of round trips
    // over a change that touches one column.

    [Fact]
    public async Task UpdateTransactionLinksAsync_RelinksAndDetachesInOneWrite()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        var linked = VaultDocumentForYear(2026, 1);
        linked.TransactionId = "tx-old";
        var unlinked = VaultDocumentForYear(2026, 2);
        context.VaultDocuments.AddRange(linked, unlinked);
        await context.SaveChangesAsync();
        var service = NewService(context, new FakeDocumentVaultStore());

        var results = await service.UpdateTransactionLinksAsync(
        [
            new TransactionLinkDocumentUpdate(1, null),
            new TransactionLinkDocumentUpdate(2, "tx-new"),
        ]);

        Assert.All(results, result => Assert.True(result.Updated));
        Assert.Null((await context.VaultDocuments.FindAsync(1))!.TransactionId);
        Assert.Equal("tx-new", (await context.VaultDocuments.FindAsync(2))!.TransactionId);
    }

    [Fact]
    public async Task UpdateTransactionLinksAsync_ClearsTheDetachedMarkerSoARestoreCannotRelink()
    {
        // Same rule the single-document path applies: an explicit re-point is the user's final word
        // on where the document belongs, so restoring the transaction it used to hang off must not
        // drag it back. Without this the bulk path would quietly disagree with the single one.
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        var document = VaultDocumentForYear(2026, 1);
        document.TransactionId = null;
        document.DetachedFromTransactionId = "tx-deleted";
        context.VaultDocuments.Add(document);
        await context.SaveChangesAsync();
        var service = NewService(context, new FakeDocumentVaultStore());

        await service.UpdateTransactionLinksAsync([new TransactionLinkDocumentUpdate(1, "tx-new")]);

        var saved = await context.VaultDocuments.FindAsync(1);
        Assert.Equal("tx-new", saved!.TransactionId);
        Assert.Null(saved.DetachedFromTransactionId);
    }

    [Fact]
    public async Task UpdateTransactionLinksAsync_ReportsAMissingDocumentWithoutFailingTheBatch()
    {
        // A partial answer is the point: the caller is reconciling many documents at once, and one
        // deleted row must not cost the others their relink.
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.Add(VaultDocumentForYear(2026, 1));
        await context.SaveChangesAsync();
        var service = NewService(context, new FakeDocumentVaultStore());

        var results = await service.UpdateTransactionLinksAsync(
        [
            new TransactionLinkDocumentUpdate(1, "tx-new"),
            new TransactionLinkDocumentUpdate(404, "tx-new"),
        ]);

        Assert.True(results.Single(result => result.Id == 1).Updated);
        var missing = results.Single(result => result.Id == 404);
        Assert.False(missing.Updated);
        Assert.NotNull(missing.Message);
        Assert.Equal("tx-new", (await context.VaultDocuments.FindAsync(1))!.TransactionId);
    }

    [Fact]
    public async Task UpdateTransactionLinksAsync_LeavesAnotherAccountsDocumentAlone()
    {
        // Tenancy fails closed: the query filter hides the row, so it must read as "not found" here
        // rather than as a relink the caller believes succeeded.
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.AppUsers.AddRange(
            new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" },
            new AppUser { Id = "other-user", Username = "other", PasswordHash = "hash" });
        var foreign = VaultDocumentForYear(2026, 7);
        foreign.UserId = "other-user";
        foreign.TransactionId = "tx-theirs";
        context.VaultDocuments.Add(foreign);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        context.SetCurrentUser("test-user");
        var service = NewService(context, new FakeDocumentVaultStore());

        var results = await service.UpdateTransactionLinksAsync([new TransactionLinkDocumentUpdate(7, "tx-mine")]);

        Assert.False(results.Single().Updated);
        context.ChangeTracker.Clear();
        context.SetCurrentUser("other-user");
        Assert.Equal("tx-theirs", (await context.VaultDocuments.FindAsync(7))!.TransactionId);
    }

    private static DocumentVaultService NewService(AppDbContext context, IDocumentVaultStore store, long maxBytes = 10 * 1024 * 1024, long maxTotalBytes = 100 * 1024 * 1024)
    {
        var options = new FixedOptionsMonitor<DocumentVaultOptions>(new DocumentVaultOptions
        {
            MaxDocumentBytes = maxBytes,
            MaxTotalBytesPerUser = maxTotalBytes
        });
        return new DocumentVaultService(context, store, options, NullLogger<DocumentVaultService>.Instance);
    }

    private static void AddTestCategory(AppDbContext context, int taxYear = 2026) =>
        context.TaxReliefCategoryLimits.Add(new TaxReliefCategoryLimit
        {
            UserId = "test-user",
            CategoryId = "test-category",
            Name = "Test category",
            Limit = 1000m,
            TaxYear = taxYear
        });

    private static VaultDocument VaultDocumentForYear(int year, int id) => new()
    {
        Id = id,
        UserId = "test-user",
        StorageObjectPath = $"test-user/{year}/{id}.pdf",
        OriginalFileName = $"{id}.pdf",
        ContentType = "application/pdf",
        SizeBytes = 10,
        Sha256 = $"hash-{id}",
        TaxYear = year,
        UploadedAt = DateTime.UtcNow.AddMinutes(-id),
        RetentionUntil = new DateOnly(year, 12, 31).AddYears(7)
    };

    private sealed class FailVaultDocumentSaveInterceptor : SaveChangesInterceptor
    {
        public bool FailVaultDocumentSaves { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailVaultDocumentSaves &&
                eventData.Context?.ChangeTracker.Entries<VaultDocument>()
                    .Any(entry => entry.State == EntityState.Added) == true)
            {
                throw new InvalidOperationException("Simulated database failure.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
