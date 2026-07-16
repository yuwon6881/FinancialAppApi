using FinancialAppApi.Controllers;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class OcrControllerTests
{
    [Fact]
    public async Task ProcessScanJob_MissingRetainedJobAcknowledgesCloudTask()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var configuration = TestHelpers.NewConfiguration(("OcrWorkerKey", "test-worker-key"));
        var policy = new ReceiptScanRetentionPolicy(configuration);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var processor = new ReceiptScanProcessor(
            new AiClient(new HttpClient(), configuration, NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache),
            NullLogger<ReceiptScanProcessor>.Instance);
        var controller = new OcrController(
            new OcrScanJobService(context, policy),
            processor,
            new ReceiptScanTaskDispatcher(
                new HttpClient(),
                configuration,
                new ReceiptScanQueue(),
                NullLogger<ReceiptScanTaskDispatcher>.Instance),
            configuration,
            NullLogger<OcrController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.Request.Headers["X-Ocr-Worker-Key"] = "test-worker-key";

        var result = await controller.ProcessScanJob("already-expired");

        Assert.IsType<OkObjectResult>(result);
    }
}
