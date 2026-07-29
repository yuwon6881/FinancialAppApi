using FinancialAppApi.Database;
using FinancialAppApi.Models;
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
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);

        var data = new byte[] { 0x00, 0x01, 0x02 };

        var result = await service.CreateAsync("file.txt", data, 2026, "Receipt", null, null, null);

        Assert.Equal(DocumentVaultCreateStatus.UnsupportedType, result.Status);
    }

    [Fact]
    public async Task CreateAsync_RejectsLargeFiles()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store, maxBytes: 10);

        var data = new byte[11];
        data[0] = 0x25; data[1] = 0x50; data[2] = 0x44; data[3] = 0x46; data[4] = 0x2D;

        var result = await service.CreateAsync("file.pdf", data, 2026, "Receipt", null, null, null);

        Assert.Equal(DocumentVaultCreateStatus.FileTooLarge, result.Status);
    }

    [Fact]
    public async Task CreateAsync_AcceptsExpiredTaxYearForManualRetentionManagement()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        await context.SaveChangesAsync();

        var service = NewService(context, new FakeDocumentVaultStore());
        var data = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };

        var result = await service.CreateAsync("file.pdf", data, 2018, "Receipt", null, null, null);

        Assert.Equal(DocumentVaultCreateStatus.Created, result.Status);
        var expired = await service.GetExpiredTaxYearsAsync();
        Assert.Single(expired);
        Assert.Equal(2018, expired[0].TaxYear);
    }

    [Fact]
    public async Task GetAvailableTaxYearsAsync_ReturnsOnlyDistinctYearsWithDocuments()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.AddRange(
            VaultDocumentForYear(2025, 1),
            VaultDocumentForYear(2026, 2),
            VaultDocumentForYear(2025, 3));
        await context.SaveChangesAsync();

        var service = NewService(context, new FakeDocumentVaultStore());

        Assert.Equal([2026, 2025], await service.GetAvailableTaxYearsAsync());
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
        await context.SaveChangesAsync();

        var summary = await NewService(context, new FakeDocumentVaultStore()).GetTaxYearSummaryAsync(2025);

        Assert.NotNull(summary);
        var lifestyle = Assert.Single(summary.Categories);
        Assert.Equal(2000, lifestyle.ConfirmedAmount);
        Assert.Equal(800, lifestyle.PendingReviewAmount);
        Assert.Equal(2500, lifestyle.Limit);
    }

    [Fact]
    public async Task CreateAsync_RejectsOverQuota()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        
        context.VaultDocuments.Add(new VaultDocument 
        { 
            UserId = "test-user",
            StorageObjectPath = "test-user/2026/1.pdf",
            OriginalFileName = "1.pdf",
            ContentType = "application/pdf",
            SizeBytes = 90,
            Sha256 = "hash",
            TaxYear = 2026,
            DocumentType = "Receipt",
            UploadedAt = DateTime.UtcNow,
            RetentionUntil = new DateOnly(2033, 12, 31)
        });
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store, maxTotalBytes: 100);

        var data = new byte[15];
        data[0] = 0x25; data[1] = 0x50; data[2] = 0x44; data[3] = 0x46; data[4] = 0x2D;

        var result = await service.CreateAsync("file.pdf", data, 2026, "Receipt", null, null, null);

        Assert.Equal(DocumentVaultCreateStatus.QuotaExceeded, result.Status);
    }

    [Fact]
    public async Task CreateAsync_AcceptsPdf()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);

        var data = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 1, 2, 3 };

        var result = await service.CreateAsync("file.pdf", data, 2026, "Receipt", null, null, null);

        Assert.Equal(DocumentVaultCreateStatus.Created, result.Status);
        Assert.NotNull(result.DocumentId);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task CreateAsync_AcceptsJpeg()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);

        var data = new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 };

        var result = await service.CreateAsync("file.jpg", data, 2026, "Receipt", null, null, null);

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
        await context.SaveChangesAsync();
        interceptor.FailVaultDocumentSaves = true;

        var store = new FakeDocumentVaultStore();
        var service = NewService(context, store);

        var data = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("file.pdf", data, 2026, "Receipt", null, null, null));

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
            DocumentType = "Receipt",
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

    private static DocumentVaultService NewService(AppDbContext context, IDocumentVaultStore store, long maxBytes = 10 * 1024 * 1024, long maxTotalBytes = 100 * 1024 * 1024)
    {
        var options = new FixedOptionsMonitor<DocumentVaultOptions>(new DocumentVaultOptions
        {
            MaxDocumentBytes = maxBytes,
            MaxTotalBytesPerUser = maxTotalBytes
        });
        return new DocumentVaultService(context, store, options, NullLogger<DocumentVaultService>.Instance);
    }

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
        DocumentType = "Receipt",
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
