using FinancialAppApi.Filters;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/ai")]
[AuthorizeToken]
[EnableRateLimiting("ai")]
public class AiController : ControllerBase
{
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

    [HttpGet("conversation")]
    public async Task<ActionResult<AiConversationResponse>> GetConversation(CancellationToken cancellationToken)
    {
        return Ok(await _conversationMemory.GetActiveAsync(cancellationToken));
    }

    [HttpDelete("conversation")]
    public async Task<IActionResult> DeleteConversation(CancellationToken cancellationToken)
    {
        await _conversationMemory.DeleteActiveAsync(cancellationToken);
        return NoContent();
    }
}
