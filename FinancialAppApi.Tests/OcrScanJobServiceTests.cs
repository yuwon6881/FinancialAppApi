using System.Text.Json;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class OcrScanJobServiceTests
{
    [Fact]
    public async Task CreateScanJobAsync_PersistsQueuedJobAndStoresDetectedImageTypeExternally()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var imageStore = new FakeReceiptImageStore();
        var service = NewService(context, imageStore);

        var result = await service.CreateScanJobAsync(
            TestHelpers.DefaultUserId,
            "alice",
            NewFormFile("receipt.heic", "application/octet-stream", HeicBytes()));

        Assert.Equal(CreateScanJobStatus.Created, result.Status);
        var job = await context.ReceiptScanJobs.SingleAsync();
        Assert.Equal("alice", job.Username);
        Assert.Equal("queued", job.Status);
        Assert.Equal("image/heic", job.MimeType);
        Assert.Equal("receipt", job.ScanType);
        Assert.EndsWith(".heic", job.StorageObjectPath, StringComparison.Ordinal);
        Assert.Equal(HeicBytes(), imageStore.Objects[job.StorageObjectPath!]);
    }

    [Fact]
    public async Task CreateScanJobAsync_PersistsInvestmentScanType()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context, new FakeReceiptImageStore());

        var result = await service.CreateScanJobAsync(
            TestHelpers.DefaultUserId,
            "alice",
            NewFormFile("activity.heic", "application/octet-stream", HeicBytes()),
            scanType: "investment");

        Assert.Equal(CreateScanJobStatus.Created, result.Status);
        Assert.Equal("investment", (await context.ReceiptScanJobs.SingleAsync()).ScanType);
    }

    [Fact]
    public async Task CreateScanJobAsync_RejectsMissingImage()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context, new FakeReceiptImageStore());

        var result = await service.CreateScanJobAsync(TestHelpers.DefaultUserId, "alice", null);

        Assert.Equal(CreateScanJobStatus.NoImage, result.Status);
        Assert.Equal("No image file provided.", result.Message);
    }

    [Fact]
    public async Task CreateScanJobAsync_RejectsUnsupportedFileContent()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var imageStore = new FakeReceiptImageStore();
        var service = NewService(context, imageStore);

        var result = await service.CreateScanJobAsync(
            TestHelpers.DefaultUserId,
            "alice",
            NewFormFile("not-an-image.txt", "image/jpeg", "not an image"u8.ToArray()));

        Assert.Equal(CreateScanJobStatus.UnsupportedImageType, result.Status);
        Assert.Empty(context.ReceiptScanJobs);
        Assert.Empty(imageStore.Objects);
    }

    [Fact]
    public async Task CreateScanJobAsync_RejectsWhenUserHasReachedOutstandingJobLimit()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.AddRange(NewJob("job-1", "alice"), NewJob("job-2", "alice"));
        await context.SaveChangesAsync();
        var service = NewService(
            context,
            new FakeReceiptImageStore(),
            ("Ocr:MaxOutstandingJobsPerUser", "2"));

        var result = await service.CreateScanJobAsync(
            TestHelpers.DefaultUserId,
            "alice",
            NewFormFile("receipt.jpg", "image/jpeg", JpegBytes()));

        Assert.Equal(CreateScanJobStatus.TooManyOutstandingJobs, result.Status);
        Assert.Equal(2, await context.ReceiptScanJobs.CountAsync());
    }

    [Fact]
    public async Task CreateScanJobAsync_WhenStorageIsUnavailable_DoesNotPersistJob()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var imageStore = new FakeReceiptImageStore { FailUploads = true };
        var service = NewService(context, imageStore);

        var result = await service.CreateScanJobAsync(
            TestHelpers.DefaultUserId,
            "alice",
            NewFormFile("receipt.jpg", "image/jpeg", JpegBytes()));

        Assert.Equal(CreateScanJobStatus.StorageUnavailable, result.Status);
        Assert.Empty(context.ReceiptScanJobs);
    }

    [Fact]
    public async Task GetScanJobAsync_ReturnsTerminalResultIdempotently()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.Add(new ReceiptScanJob
        {
            Id = "job-1",
            Username = "alice",
            Status = "completed",
            ResultJson = """{"description":"Cafe"}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var service = NewService(context, new FakeReceiptImageStore());

        var result = await service.GetScanJobAsync(TestHelpers.DefaultUserId, "job-1");
        var repeatedResult = await service.GetScanJobAsync(TestHelpers.DefaultUserId, "job-1");

        Assert.NotNull(result);
        Assert.Equal("completed", result.Status);
        Assert.NotNull(repeatedResult);
        Assert.Equal("completed", repeatedResult.Status);
        Assert.NotNull(await context.ReceiptScanJobs.FindAsync("job-1"));
    }

    [Fact]
    public async Task GetScanJobAsync_PreservesFullInvestmentScanPrecision()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.Add(new ReceiptScanJob
        {
            Id = "investment-precision",
            UserId = TestHelpers.DefaultUserId,
            Username = "alice",
            Status = "completed",
            ScanType = "investment",
            ResultJson = """{"units":1.234567891,"unitPrice":123.456789123,"cashAmount":152.415787501} """,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var service = NewService(context, new FakeReceiptImageStore());

        var response = await service.GetScanJobAsync(TestHelpers.DefaultUserId, "investment-precision");

        Assert.NotNull(response?.Result);
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(response.Result));
        Assert.Equal(1.234567891m, FinancialAppApi.Database.ObfuscationHelper.Deobfuscate(result.RootElement.GetProperty("units").GetString()!));
        Assert.Equal(123.456789123m, FinancialAppApi.Database.ObfuscationHelper.Deobfuscate(result.RootElement.GetProperty("unitPrice").GetString()!));
        Assert.Equal(152.415787501m, FinancialAppApi.Database.ObfuscationHelper.Deobfuscate(result.RootElement.GetProperty("cashAmount").GetString()!));
    }

    [Fact]
    public async Task DeleteScanJobAsync_RemovesOnlyMatchingUsersJobAndObject()
    {
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.ReceiptScanJobs.AddRange(
            NewJob("job-1", "alice", "alice-user"),
            NewJob("job-2", "bob", "bob-user"));
        await context.SaveChangesAsync();
        var imageStore = new FakeReceiptImageStore();
        imageStore.Objects.Add("alice-user/job-1.jpg", JpegBytes());
        imageStore.Objects.Add("bob-user/job-2.jpg", JpegBytes());
        var service = NewService(context, imageStore);

        var deleted = await service.DeleteScanJobAsync("alice-user", "job-1");

        Assert.True(deleted);
        Assert.False(await context.ReceiptScanJobs.AnyAsync(j => j.Id == "job-1" && j.Username == "alice"));
        Assert.True(await context.ReceiptScanJobs.AnyAsync(j => j.Id == "job-2" && j.Username == "bob"));
        Assert.DoesNotContain("alice-user/job-1.jpg", imageStore.Objects.Keys);
        Assert.Contains("bob-user/job-2.jpg", imageStore.Objects.Keys);
    }

    [Fact]
    public async Task DeleteScanJobAsync_WhenObjectDeleteFails_RetainsReferenceForCleanup()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.Add(NewJob("job-1", "alice"));
        await context.SaveChangesAsync();
        var imageStore = new FakeReceiptImageStore { FailDeletes = true };
        var service = NewService(context, imageStore);

        var deleted = await service.DeleteScanJobAsync(TestHelpers.DefaultUserId, "job-1");

        Assert.False(deleted);
        Assert.NotNull(await context.ReceiptScanJobs.FindAsync("job-1"));
    }

    [Fact]
    public async Task MarkDispatchFailedAsync_UpdatesJobAndDeletesStoredImage()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.Add(NewJob("job-1", "alice"));
        await context.SaveChangesAsync();
        var imageStore = new FakeReceiptImageStore();
        imageStore.Objects.Add($"{TestHelpers.DefaultUserId}/job-1.jpg", JpegBytes());
        var service = NewService(context, imageStore);

        await service.MarkDispatchFailedAsync("job-1");

        var job = (await context.ReceiptScanJobs.FindAsync("job-1"))!;
        Assert.Equal("failed", job.Status);
        Assert.Null(job.StorageObjectPath);
        Assert.Empty(imageStore.Objects);
        Assert.Equal("Could not start receipt scan. Please try again.", job.ErrorMessage);
        Assert.NotNull(job.CompletedAt);
    }

    private static OcrScanJobService NewService(
        Database.AppDbContext context,
        IReceiptImageStore imageStore,
        params (string Key, string Value)[] configuration)
    {
        return new OcrScanJobService(
            context,
            new ReceiptScanRetentionPolicy(TestHelpers.NewConfiguration(configuration)),
            imageStore,
            NullLogger<OcrScanJobService>.Instance);
    }

    private static ReceiptScanJob NewJob(string id, string username, string userId = TestHelpers.DefaultUserId)
    {
        return new ReceiptScanJob
        {
            Id = id,
            UserId = userId,
            Username = username,
            Status = "queued",
            MimeType = "image/jpeg",
            StorageObjectPath = $"{userId}/{id}.jpg",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static IFormFile NewFormFile(string fileName, string contentType, byte[] contents)
    {
        var stream = new MemoryStream(contents);
        return new FormFile(stream, 0, stream.Length, "image", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    private static byte[] JpegBytes() => [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    private static byte[] HeicBytes() =>
        [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c'];
}
