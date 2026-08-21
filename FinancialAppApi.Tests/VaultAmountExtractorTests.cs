using System.Net;
using System.Text;
using System.Text.Json;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Documents;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

/// <summary>
/// The extractor reads a suggestion off a document; a person confirms it afterwards. These cover
/// what it does with an answer it cannot use, which is the only part a person cannot correct after
/// the fact — an amount filed under the wrong currency looks exactly like one filed under the right
/// one, because the Vault renders every figure in the app's own currency.
/// </summary>
public class VaultAmountExtractorTests
{
    [Fact]
    public async Task ExtractAsync_KeepsAnAmountWhoseCurrencyTheDocumentStates()
    {
        var extractor = ExtractorReturning("""{"amount":120.5,"currency":"MYR","confidence":0.9}""");

        var result = await extractor.ExtractAsync("receipt.pdf", "application/pdf", [1, 2, 3]);

        Assert.Equal(120.5m, result.Amount);
        Assert.Equal("MYR", result.Currency);
        Assert.Equal("NeedsReview", result.Status);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task ExtractAsync_DoesNotFileAnUnknownCurrencyAmountAsRinggit()
    {
        // UNKNOWN is one of the three answers the schema allows. It used to be rewritten to MYR,
        // which put a figure the document never denominated into the ringgit relief tracker.
        var extractor = ExtractorReturning("""{"amount":120.5,"currency":"UNKNOWN","confidence":0.9}""");

        var result = await extractor.ExtractAsync("receipt.pdf", "application/pdf", [1, 2, 3]);

        Assert.Null(result.Amount);
        Assert.Equal("NotFound", result.Status);
        Assert.Contains("currency", result.Message);
    }

    [Fact]
    public async Task ExtractAsync_TreatsAnOffVocabularyCurrencyTheSameWayAsUnknown()
    {
        var extractor = ExtractorReturning("""{"amount":120.5,"currency":"USD","confidence":0.9}""");

        var result = await extractor.ExtractAsync("receipt.pdf", "application/pdf", [1, 2, 3]);

        Assert.Null(result.Amount);
        Assert.Equal("NotFound", result.Status);
    }

    [Fact]
    public async Task ExtractAsync_ReportsAMissingAmountAsNotFoundRatherThanZero()
    {
        var extractor = ExtractorReturning("""{"amount":null,"currency":"MYR","confidence":0.1}""");

        var result = await extractor.ExtractAsync("receipt.pdf", "application/pdf", [1, 2, 3]);

        Assert.Null(result.Amount);
        Assert.Equal("NotFound", result.Status);
        Assert.Contains("No reliable amount", result.Message);
    }

    [Fact]
    public async Task ExtractAsync_ReportsThatExtractionIsUnavailableWhenNoKeyIsConfigured()
    {
        var extractor = new VaultAmountExtractor(
            new AiClient(
                new HttpClient(new StubHandler(SuccessResponse("{}"))),
                TestHelpers.NewConfiguration(("OpenAiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            NullLogger<VaultAmountExtractor>.Instance);

        var result = await extractor.ExtractAsync("receipt.pdf", "application/pdf", [1, 2, 3]);

        Assert.Null(result.Amount);
        Assert.Equal("Unavailable", result.Status);
    }

    private static VaultAmountExtractor ExtractorReturning(string modelJson) =>
        new(
            new AiClient(
                new HttpClient(new StubHandler(SuccessResponse(modelJson))),
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            NullLogger<VaultAmountExtractor>.Instance);

    private static string SuccessResponse(string text) =>
        $$"""
        {
          "status": "completed",
          "output": [{ "type": "message", "content": [{ "type": "output_text", "text": {{JsonSerializer.Serialize(text)}} }] }],
          "usage": { "input_tokens": 4, "output_tokens": 2 }
        }
        """;

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
