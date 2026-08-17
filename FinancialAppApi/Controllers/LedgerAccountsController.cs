using System.Globalization;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Filters;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/accounts")]
[AuthorizeToken]
public sealed class LedgerAccountsController : ControllerBase
{
    private readonly LedgerAccountService _accountService;

    public LedgerAccountsController(LedgerAccountService accountService)
    {
        _accountService = accountService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LedgerAccountDto>>> GetAccounts()
    {
        var accounts = await _accountService.GetAccountsAsync(HttpContext.RequestAborted);
        var balances = await _accountService.GetBalancesAsync(accounts, HttpContext.RequestAborted);
        return Ok(accounts.Select(account => MapToDto(account, balances)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<LedgerAccountDto>> PostAccount(LedgerAccountMutationDto dto)
    {
        var result = await _accountService.CreateAsync(ToMutation(dto), HttpContext.RequestAborted);
        if (result.Status == LedgerAccountMutationStatus.Conflict)
            return Conflict(new { message = result.Message });
        if (result.Status != LedgerAccountMutationStatus.Success)
            return BadRequest(new { message = result.Message });

        var accounts = await _accountService.GetAccountsAsync(HttpContext.RequestAborted);
        var balances = await _accountService.GetBalancesAsync(accounts, HttpContext.RequestAborted);
        return CreatedAtAction(
            nameof(GetAccounts),
            null,
            MapToDto(result.Account!, balances));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<LedgerAccountDto>> PutAccount(string id, LedgerAccountMutationDto dto)
    {
        var result = await _accountService.UpdateAsync(id, ToMutation(dto, id), HttpContext.RequestAborted);
        if (result.Status == LedgerAccountMutationStatus.NotFound) return NotFound();
        if (result.Status == LedgerAccountMutationStatus.Conflict)
            return Conflict(new { message = result.Message });
        if (result.Status != LedgerAccountMutationStatus.Success)
            return BadRequest(new { message = result.Message });

        var accounts = await _accountService.GetAccountsAsync(HttpContext.RequestAborted);
        var balances = await _accountService.GetBalancesAsync(accounts, HttpContext.RequestAborted);
        return Ok(MapToDto(result.Account!, balances));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteAccount(string id)
    {
        var result = await _accountService.DeleteAsync(id, HttpContext.RequestAborted);
        if (result.Status == LedgerAccountMutationStatus.NotFound) return NotFound();
        if (result.Status == LedgerAccountMutationStatus.Conflict)
            return Conflict(new { message = result.Message, activityCount = result.ActivityCount });
        return NoContent();
    }

    [HttpPost("reconcile")]
    public async Task<IActionResult> Reconcile(LedgerAccountReconcileDto dto)
    {
        if (dto.Targets is null)
            return BadRequest(new { message = "At least one account target is required." });

        var request = new LedgerAccountReconcileRequest(
            dto.OperationId ?? string.Empty,
            dto.Bucket ?? string.Empty,
            ReadAmount(dto.ExpectedBucketTotal),
            dto.Targets.Select(target => new LedgerAccountReconcileTarget(
                target.Id,
                target.Name ?? string.Empty,
                target.Kind,
                target.IsArchived,
                ReadAmount(target.ExpectedCurrent),
                ReadAmount(target.Target),
                target.ExpectedName,
                target.ExpectedKind,
                target.ExpectedIsArchived)).ToList(),
            dto.Description);
        var result = await _accountService.ReconcileAsync(request, HttpContext.RequestAborted);
        if (result.Status == LedgerAccountMutationStatus.Conflict)
            return Conflict(new { message = result.Message });
        if (result.Status != LedgerAccountMutationStatus.Success)
            return BadRequest(new { message = result.Message });

        var accounts = result.Accounts ?? [];
        var balances = await _accountService.GetBalancesAsync(accounts, HttpContext.RequestAborted);
        return Ok(new
        {
            accounts = accounts.Select(account => MapToDto(account, balances)).ToList(),
            transactions = (result.Transactions ?? []).Select(transaction => new
            {
                id = transaction.Id,
                date = transaction.Date.ToUniversalTime().ToString("O"),
                description = transaction.Description,
                category = transaction.Category,
                ledgerCategory = transaction.LedgerCategory,
                amount = ObfuscationHelper.Obfuscate(transaction.Amount),
                accountId = transaction.AccountId,
                counterAccountId = transaction.CounterAccountId,
                isAccountBalanceAdjustment = transaction.IsAccountBalanceAdjustment,
                stabilityReloadIntent = transaction.StabilityReloadIntent,
            }).ToList(),
        });
    }

    internal static LedgerAccountDto MapToDto(
        LedgerAccount account,
        IReadOnlyDictionary<string, decimal> balances) => new()
    {
        Id = account.Id,
        Name = account.Name,
        Bucket = account.Bucket,
        Kind = account.Kind,
        IsArchived = account.IsArchived,
        Remaining = ObfuscationHelper.Obfuscate(
            balances.TryGetValue(account.Id, out var balance) ? balance : 0m),
        CreatedAt = account.CreatedAt.ToUniversalTime().ToString("O"),
        UpdatedAt = account.UpdatedAt.ToUniversalTime().ToString("O"),
    };

    private static LedgerAccountMutation ToMutation(LedgerAccountMutationDto dto, string? id = null) =>
        new(
            id ?? dto.Id ?? string.Empty,
            dto.Name ?? string.Empty,
            dto.Bucket ?? string.Empty,
            dto.Kind ?? LedgerAccountKind.Bank,
            dto.IsArchived,
            ReadAmount(dto.OpeningAmount));

    private static decimal ReadAmount(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind != JsonValueKind.String) return 0m;
        var text = value.GetString() ?? string.Empty;
        return ObfuscationHelper.TryDeobfuscate(text, out var obfuscated)
            ? obfuscated
            : decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var plain)
                ? plain
                : 0m;
    }
}

public sealed class LedgerAccountMutationDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Bucket { get; set; }
    public string? Kind { get; set; }
    public bool IsArchived { get; set; }
    public JsonElement OpeningAmount { get; set; }
}

public sealed class LedgerAccountDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Kind { get; set; } = LedgerAccountKind.Bank;
    public bool IsArchived { get; set; }
    public string Remaining { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public sealed class LedgerAccountReconcileDto
{
    public string? OperationId { get; set; }
    public string? Bucket { get; set; }
    public JsonElement ExpectedBucketTotal { get; set; }
    public string? Description { get; set; }
    public List<LedgerAccountReconcileTargetDto>? Targets { get; set; }
}

public sealed class LedgerAccountReconcileTargetDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? ExpectedName { get; set; }
    public string? ExpectedKind { get; set; }
    public bool? ExpectedIsArchived { get; set; }
    public bool IsArchived { get; set; }
    public JsonElement ExpectedCurrent { get; set; }
    public JsonElement Target { get; set; }
}
