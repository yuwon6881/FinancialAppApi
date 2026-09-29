using FinancialAppApi.Filters;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json;
using FinancialAppApi.Contracts;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/ai")]
[AuthorizeToken]
[EnableRateLimiting("ai")]
[NoFinancialRefresh]
public class AiController : ControllerBase
{
    public sealed record ResolveAiActionBatchRequest(string Resolution);
    private readonly AiAssistantService _aiAssistantService;
    private readonly AiConversationMemoryService _conversationMemory;
    private readonly ILogger<AiController> _logger;

    public AiController(
        AiAssistantService aiAssistantService,
        AiConversationMemoryService conversationMemory,
        ILogger<AiController> logger)
    {
        _aiAssistantService = aiAssistantService;
        _conversationMemory = conversationMemory;
        _logger = logger;
    }

    [HttpPost("chat")]
    public async Task<ActionResult<AiChatResponse>> Chat([FromBody] AiChatRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await _aiAssistantService.ChatAsync(request, cancellationToken);
            if (outcome.IsConflict) return Conflict(outcome.Response);
            return outcome.IsProviderError ? StatusCode(503, outcome.Response) : Ok(outcome.Response);
        }
        // A browser disconnect surfaces as a plain OperationCanceledException from the
        // pipeline; that is not a server fault and must not be logged as an error.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "AI chat request timed out.");
            return StatusCode(503, new { reply = "AI service timed out. Please try again.", actions = Array.Empty<object>() });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "AI chat returned invalid JSON.");
            return StatusCode(503, new { reply = "AI returned an unreadable response. Please try again.", actions = Array.Empty<object>() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while handling AI chat request.");
            return StatusCode(503, new { reply = "AI is unavailable. Please try again.", actions = Array.Empty<object>() });
        }
    }

    // The same turn as POST chat, streamed as server-sent events (see AiServerSentEvents). Once
    // the stream starts the HTTP status is 200, so failures arrive as an error event carrying
    // the status the JSON endpoint would have used.
    [HttpPost("chat/stream")]
    public async Task ChatStream([FromBody] AiChatRequest request, CancellationToken cancellationToken)
    {
        await using var events = await AiServerSentEvents.StartAsync(Response, cancellationToken);
        try
        {
            var outcome = await _aiAssistantService.ChatAsync(request, events, cancellationToken);
            if (outcome.IsConflict) await events.ErrorAsync(StatusCodes.Status409Conflict, outcome.Response, cancellationToken);
            else if (outcome.IsProviderError) await events.ErrorAsync(StatusCodes.Status503ServiceUnavailable, outcome.Response, cancellationToken);
            else await events.DoneAsync(outcome.Response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client stopped the turn or went away; there is no one left to tell.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while streaming an AI chat turn.");
            await events.ErrorAsync(
                StatusCodes.Status503ServiceUnavailable,
                new { reply = "AI is unavailable. Please try again.", actions = Array.Empty<object>() },
                CancellationToken.None);
        }
    }

    [HttpGet("conversation")]
    public async Task<ActionResult<AiConversationResponse>> GetConversation(
        [FromQuery] bool forceSensitiveMode,
        CancellationToken cancellationToken)
    {
        return Ok(await _conversationMemory.GetActiveAsync(forceSensitiveMode, cancellationToken));
    }

    [HttpDelete("conversation")]
    public async Task<IActionResult> DeleteConversation(
        [FromQuery] Guid? conversationId,
        [FromQuery] int? expectedVersion,
        CancellationToken cancellationToken)
    {
        return await _conversationMemory.DeleteActiveAsync(conversationId, expectedVersion, cancellationToken)
            ? NoContent()
            : Conflict(new { reply = "This conversation changed on another device. Reload it before starting a new chat." });
    }

    [HttpPost("action-batches/{batchId:guid}/resolve")]
    [RefreshSlices(RefreshSliceNames.Core)]
    public async Task<IActionResult> ResolveActionBatch(
        Guid batchId,
        [FromBody] ResolveAiActionBatchRequest request,
        CancellationToken cancellationToken)
    {
        var dismissed = request.Resolution.Equals("dismissed", StringComparison.OrdinalIgnoreCase);
        if (!dismissed && !request.Resolution.Equals("accepted", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Resolution must be accepted or dismissed." });
        return await _conversationMemory.ResolveActionBatchAsync(batchId, dismissed, cancellationToken)
            ? NoContent()
            : NotFound();
    }
}
