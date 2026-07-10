using FinancialAppApi.Filters;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/ai")]
[AuthorizeToken]
public class AiController : ControllerBase
{
    private readonly AiAssistantService _aiAssistantService;
    private readonly ILogger<AiController> _logger;

    public AiController(AiAssistantService aiAssistantService, ILogger<AiController> logger)
    {
        _aiAssistantService = aiAssistantService;
        _logger = logger;
    }

    [HttpPost("chat")]
    public async Task<ActionResult<AiChatResponse>> Chat([FromBody] AiChatRequest request)
    {
        try
        {
            return Ok(await _aiAssistantService.ChatAsync(request));
        }
        catch (AiAssistantService.AiAssistantUserException ex)
        {
            return StatusCode(503, new { reply = ex.Message, actions = Array.Empty<object>() });
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
    }
}
