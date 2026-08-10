using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Documents;

namespace FinancialAppApi.Tests;

public class DocumentRetentionServiceTests
{
    // Every date is derived from the clock the service actually uses, so these cannot rot: there is
    // no public seam for a fake clock here, and absolute dates would start failing on a fixed day.
    private static readonly DateOnly Today = FinancialClock.Utc.Today;

    [Fact]
    public async Task ExistingExpiredDocumentsRemainReadableAndAreReportedForManualReview()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.Add(Document(1, 2018, DocumentRetentionService.RetentionUntilFor(2018)));
        await context.SaveChangesAsync();

        var review = await new DocumentRetentionService(context).GetReviewAsync();

        var year = Assert.Single(review.TaxYears);
        Assert.Equal(2018, year.TaxYear);
        Assert.True(year.DaysUntilKeepUntil < 0, "A 2018 record is long past the date it is worth keeping.");
        Assert.Equal(DocumentRetentionService.KeepYears, review.KeepYears);
    }

    [Fact]
    public async Task ARecordApproachingItsKeepUntilDateIsReportedBeforeItPasses()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.Add(Document(1, 2020, Today.AddDays(30)));
        await context.SaveChangesAsync();

        var review = await new DocumentRetentionService(context).GetReviewAsync();

        var year = Assert.Single(review.TaxYears);
        // The whole point of the notice: warned while there is still time to act, not only after.
        Assert.Equal(30, year.DaysUntilKeepUntil);
    }

    [Fact]
    public async Task ARecordBeyondTheNoticeWindowIsNotReportedYet()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.Add(Document(1, 2024, Today.AddDays(400)));
        await context.SaveChangesAsync();

        var review = await new DocumentRetentionService(context).GetReviewAsync();

        Assert.Empty(review.TaxYears);
        Assert.True(review.NoticeWindowDays is > 0 and < 400);
    }

    [Fact]
    public async Task OneTaxYearReportsOnceCarryingTheEarliestKeepUntilAndTheSummedSize()
    {
        // Two documents filed under the same year with different keep-until dates used to produce two
        // summaries for that year, which the notice rendered as "2019 (1), 2019 (1)".
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.Add(Document(1, 2019, Today.AddDays(-5), sizeBytes: 100));
        context.VaultDocuments.Add(Document(2, 2019, Today.AddDays(20), sizeBytes: 250));
        await context.SaveChangesAsync();

        var review = await new DocumentRetentionService(context).GetReviewAsync();

        var year = Assert.Single(review.TaxYears);
        Assert.Equal(2, year.DocumentCount);
        Assert.Equal(350, year.TotalBytes);
        Assert.Equal(Today.AddDays(-5), year.KeepUntil);
        Assert.Equal(-5, year.DaysUntilKeepUntil);
    }

    [Fact]
    public async Task MostUrgentYearIsReportedFirst()
    {
        await using var context = TestHelpers.NewInMemoryContext("test-user");
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.Add(Document(1, 2021, Today.AddDays(60)));
        context.VaultDocuments.Add(Document(2, 2018, Today.AddDays(-400)));
        context.VaultDocuments.Add(Document(3, 2020, Today.AddDays(10)));
        await context.SaveChangesAsync();

        var review = await new DocumentRetentionService(context).GetReviewAsync();

        Assert.Equal([2018, 2020, 2021], review.TaxYears.Select(year => year.TaxYear));
    }

    [Fact]
    public void KeepUntilIsTheEndOfTheTaxYearPlusTheKeepPeriod()
    {
        Assert.Equal(new DateOnly(2033, 12, 31), DocumentRetentionService.RetentionUntilFor(2026));
    }

    private static VaultDocument Document(int id, int taxYear, DateOnly keepUntil, long sizeBytes = 10) => new()
    {
        Id = id,
        UserId = "test-user",
        StorageObjectPath = $"test-user/{taxYear}/{id}.pdf",
        OriginalFileName = $"{id}.pdf",
        ContentType = "application/pdf",
        SizeBytes = sizeBytes,
        Sha256 = $"hash-{id}",
        TaxYear = taxYear,
        UploadedAt = DateTime.UtcNow.AddMinutes(-id),
        RetentionUntil = keepUntil
    };
}
