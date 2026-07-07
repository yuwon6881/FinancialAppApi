using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FinancialAppApi.Services;

public class ReceiptScanTaskDispatcher
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReceiptScanTaskDispatcher> _logger;

    public ReceiptScanTaskDispatcher(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<ReceiptScanTaskDispatcher> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task DispatchAsync(string jobId)
    {
        if (HasCloudTasksConfig())
        {
            await CreateCloudTaskAsync(jobId);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ReceiptScanProcessor>();
                await processor.ProcessAsync(jobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallback receipt scan processor failed for job {JobId}.", jobId);
            }
        });
    }

    private bool HasCloudTasksConfig()
    {
        return !string.IsNullOrWhiteSpace(_configuration["CloudTasks:ProjectId"]) &&
               !string.IsNullOrWhiteSpace(_configuration["CloudTasks:LocationId"]) &&
               !string.IsNullOrWhiteSpace(_configuration["CloudTasks:QueueId"]) &&
               !string.IsNullOrWhiteSpace(_configuration["CloudTasks:WorkerBaseUrl"]) &&
               !string.IsNullOrWhiteSpace(_configuration["OcrWorkerKey"]);
    }

    private async Task CreateCloudTaskAsync(string jobId)
    {
        var projectId = _configuration["CloudTasks:ProjectId"]!;
        var locationId = _configuration["CloudTasks:LocationId"]!;
        var queueId = _configuration["CloudTasks:QueueId"]!;
        var workerBaseUrl = _configuration["CloudTasks:WorkerBaseUrl"]!.TrimEnd('/');
        var workerKey = _configuration["OcrWorkerKey"]!;

        var accessToken = await GetMetadataAccessTokenAsync();
        var createTaskUrl = $"https://cloudtasks.googleapis.com/v2/projects/{projectId}/locations/{locationId}/queues/{queueId}/tasks";
        var processUrl = $"{workerBaseUrl}/api/ocr/scan-receipt/jobs/{jobId}/process";

        var body = new
        {
            task = new
            {
                httpRequest = new
                {
                    httpMethod = "POST",
                    url = processUrl,
                    headers = new Dictionary<string, string>
                    {
                        ["X-Ocr-Worker-Key"] = workerKey,
                        ["Content-Type"] = "application/json"
                    },
                    body = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, createTaskUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("Cloud Tasks create failed for receipt scan job {JobId}. Status {Status}: {Body}", jobId, response.StatusCode, responseBody);
            throw new InvalidOperationException("Could not enqueue receipt scan job.");
        }
    }

    private static async Task<string> GetMetadataAccessTokenAsync()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token");
        request.Headers.Add("Metadata-Flavor", "Google");

        using var response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Metadata server returned no access token.");
    }
}
