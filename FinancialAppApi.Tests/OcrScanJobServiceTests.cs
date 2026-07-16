using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class OcrScanJobServiceTests
{
    [Fact]
    public async Task CreateScanJobAsync_PersistsQueuedJobWithDetectedMimeType()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);

        var result = await service.CreateScanJobAsync(
            "alice",
            NewFormFile("receipt.heic", "application/octet-stream", HeicBytes()));

        Assert.Equal(CreateScanJobStatus.Created, result.Status);
        var job = await context.ReceiptScanJobs.SingleAsync();
        Assert.Equal("alice", job.Username);
        Assert.Equal("queued", job.Status);
        Assert.Equal("image/heic", job.MimeType);
        Assert.Equal(HeicBytes(), job.ImageData);
    }

    [Fact]
    public async Task CreateScanJobAsync_RejectsMissingImage()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);

        var result = await service.CreateScanJobAsync("alice", null);

        Assert.Equal(CreateScanJobStatus.NoImage, result.Status);
        Assert.Equal("No image file provided.", result.Message);
    }

    [Fact]
    public async Task CreateScanJobAsync_RejectsUnsupportedFileContent()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);

        var result = await service.CreateScanJobAsync(
            "alice",
            NewFormFile("not-an-image.txt", "image/jpeg", "not an image"u8.ToArray()));

        Assert.Equal(CreateScanJobStatus.UnsupportedImageType, result.Status);
        Assert.Empty(context.ReceiptScanJobs);
    }

    [Fact]
    public async Task CreateScanJobAsync_RejectsWhenUserHasReachedOutstandingJobLimit()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.AddRange(NewJob("job-1", "alice"), NewJob("job-2", "alice"));
        await context.SaveChangesAsync();
        var service = NewService(context, ("Ocr:MaxOutstandingJobsPerUser", "2"));

        var result = await service.CreateScanJobAsync(
            "alice",
            NewFormFile("receipt.jpg", "image/jpeg", JpegBytes()));

        Assert.Equal(CreateScanJobStatus.TooManyOutstandingJobs, result.Status);
        Assert.Equal(2, await context.ReceiptScanJobs.CountAsync());
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
        var service = NewService(context);

        var result = await service.GetScanJobAsync("alice", "job-1");
        var repeatedResult = await service.GetScanJobAsync("alice", "job-1");

        Assert.NotNull(result);
        Assert.Equal("completed", result.Status);
        Assert.NotNull(repeatedResult);
        Assert.Equal("completed", repeatedResult.Status);
        Assert.NotNull(await context.ReceiptScanJobs.FindAsync("job-1"));
    }

    [Fact]
    public async Task DeleteScanJobAsync_RemovesOnlyMatchingUsersJob()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.AddRange(
            NewJob("job-1", "alice"),
            NewJob("job-2", "bob"));
        await context.SaveChangesAsync();
        var service = NewService(context);

        await service.DeleteScanJobAsync("alice", "job-1");

        Assert.False(await context.ReceiptScanJobs.AnyAsync(j => j.Id == "job-1" && j.Username == "alice"));
        Assert.True(await context.ReceiptScanJobs.AnyAsync(j => j.Id == "job-2" && j.Username == "bob"));
    }

    [Fact]
    public async Task MarkDispatchFailedAsync_UpdatesJobAndClearsImage()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.ReceiptScanJobs.Add(NewJob("job-1", "alice"));
        await context.SaveChangesAsync();
        var service = NewService(context);

        await service.MarkDispatchFailedAsync("job-1");

        var job = (await context.ReceiptScanJobs.FindAsync("job-1"))!;
        Assert.Equal("failed", job.Status);
        Assert.Null(job.ImageData);
        Assert.Equal("Could not start receipt scan. Please try again.", job.ErrorMessage);
        Assert.NotNull(job.CompletedAt);
    }

    private static OcrScanJobService NewService(
        Database.AppDbContext context,
        params (string Key, string Value)[] configuration)
    {
        return new OcrScanJobService(
            context,
            new ReceiptScanRetentionPolicy(TestHelpers.NewConfiguration(configuration)));
    }

    private static ReceiptScanJob NewJob(string id, string username)
    {
        return new ReceiptScanJob
        {
            Id = id,
            Username = username,
            Status = "queued",
            MimeType = "image/jpeg",
            ImageData = JpegBytes(),
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
