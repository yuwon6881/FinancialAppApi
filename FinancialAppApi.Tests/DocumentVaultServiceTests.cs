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
    public async Task ExistingExpiredDocumentsRemainReadableAndAreReportedForManualReview()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.Add(VaultDocumentForYear(2018, 1));
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);

        var expired = await service.GetExpiredTaxYearsAsync();
        var listed = await service.ListAsync(2018, null, null, "uploaded-desc", 0, 10);

        Assert.Single(expired);
        Assert.Equal(2018, expired[0].TaxYear);
        Assert.Single(listed.Items);
        Assert.Equal(2018, listed.Items[0].TaxYear);
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
            Detail = "User-maintained limit",
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
            Detail = "Prior year",
            TaxYear = 2025
        });
        await context.SaveChangesAsync();

        var service = NewService(context, new FakeDocumentVaultStore());
        var inherited = await service.GetReliefCategoriesAsync(2026);
        var inheritedLifestyle = Assert.Single(inherited);
        Assert.True(inheritedLifestyle.IsInherited);
        Assert.Equal(2500m, inheritedLifestyle.Limit);

        var update = await service.UpdateReliefCategoryAsync(
            2026, "lifestyle", "Lifestyle", 3000m, "Current year", CancellationToken.None);

        Assert.Equal(TaxReliefCategoryMutationStatus.Saved, update.Status);
        Assert.False(Assert.Single(await service.GetReliefCategoriesAsync(2026)).IsInherited);
        Assert.Equal(3000m, Assert.Single(await service.GetReliefCategoriesAsync(2026)).Limit);
        Assert.Equal(2500m, Assert.Single(await service.GetReliefCategoriesAsync(2025)).Limit);

        var add = await service.AddReliefCategoryAsync(2026, "Sports", 1000m, "New category");
        Assert.Equal(TaxReliefCategoryMutationStatus.Saved, add.Status);
        Assert.Single(await service.GetReliefCategoriesAsync(2025));
        Assert.Collection(await service.GetReliefCategoriesAsync(2026), _ => { }, _ => { });
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

        Assert.False(result);
        Assert.NotNull(await context.VaultDocuments.FirstOrDefaultAsync(d => d.Id == 1));
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
            Detail = "Test configuration",
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
