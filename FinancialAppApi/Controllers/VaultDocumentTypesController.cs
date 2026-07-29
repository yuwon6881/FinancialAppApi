using FinancialAppApi.Filters;
using FinancialAppApi.Services.Documents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/document-types")]
[AuthorizeToken]
[EnableRateLimiting("documents")]
public sealed class VaultDocumentTypesController : ControllerBase
{
    private readonly VaultDocumentTypeService _service;

    public VaultDocumentTypesController(VaultDocumentTypeService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var types = await _service.ListAsync(ct);
        return Ok(types);
    }

    [HttpPost]
    public async Task<IActionResult> Create(DocumentTypeMutation request, CancellationToken ct)
    {
        var result = await _service.CreateAsync(request.Name, request.Id, ct);
        return result.Type == null
            ? BadRequest(new { message = result.Error })
            : Ok(result.Type);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] string? replacementId, CancellationToken ct)
    {
        var result = await _service.DeleteAsync(id, replacementId, ct);
        return result.Success
            ? NoContent()
            : Conflict(new { message = result.Error });
    }

    [HttpPost("cleanup/review")]
    public async Task<IActionResult> Review(CancellationToken ct) =>
        Ok(new { suggestions = await _service.ReviewAsync(ct) });

    [HttpPost("cleanup/apply")]
    public async Task<IActionResult> Apply(VaultTypeCleanupAction request, CancellationToken ct)
    {
        var result = await _service.ApplyAsync(request, ct);
        return result.Success
            ? Ok(new { appliedCount = 1 })
            : Conflict(new { message = result.Error });
    }
}

public sealed record DocumentTypeMutation(string? Id, string? Name);
