using System.Globalization;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Filters;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;
using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Contracts;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/accounts")]
[AuthorizeToken]
[RefreshSlices(RefreshSliceNames.Core)]
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
        var statements = await _accountService.GetCardStatementsAsync(accounts, HttpContext.RequestAborted);
        return Ok(accounts.Select(account => MapToDto(account, balances, statements)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<LedgerAccountDto>> PostAccount(LedgerAccountMutationDto dto)
    {
        var mutation = ToMutation(dto);
        if (mutation is null) return BadRequest(new { message = "Enter valid account amounts." });
        var result = await _accountService.CreateAsync(mutation, HttpContext.RequestAborted);
        if (result.Status == LedgerAccountMutationStatus.Conflict)
            return Conflict(new { message = result.Message });
        if (result.Status != LedgerAccountMutationStatus.Success)
            return BadRequest(new { message = result.Message });

        var accounts = await _accountService.GetAccountsAsync(HttpContext.RequestAborted);
        var balances = await _accountService.GetBalancesAsync(accounts, HttpContext.RequestAborted);
        var statements = await _accountService.GetCardStatementsAsync(accounts, HttpContext.RequestAborted);
        return CreatedAtAction(
            nameof(GetAccounts),
            null,
            MapToDto(result.Account!, balances, statements));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<LedgerAccountDto>> PutAccount(string id, LedgerAccountMutationDto dto)
    {
        var mutation = ToMutation(dto, id);
        if (mutation is null) return BadRequest(new { message = "Enter valid account amounts." });
        var result = await _accountService.UpdateAsync(id, mutation, HttpContext.RequestAborted);
        if (result.Status == LedgerAccountMutationStatus.NotFound) return NotFound();
        if (result.Status == LedgerAccountMutationStatus.Conflict)
            return Conflict(new { message = result.Message });
        if (result.Status != LedgerAccountMutationStatus.Success)
            return BadRequest(new { message = result.Message });

        var accounts = await _accountService.GetAccountsAsync(HttpContext.RequestAborted);
        var balances = await _accountService.GetBalancesAsync(accounts, HttpContext.RequestAborted);
        var statements = await _accountService.GetCardStatementsAsync(accounts, HttpContext.RequestAborted);
        return Ok(MapToDto(result.Account!, balances, statements));
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

        if (ReadAmount(dto.ExpectedBucketTotal) is not { } bucketTotal
            || !TryReadOptionalAmount(dto.ExpectedRewardsCommitted, out var expectedRewardsCommitted, out _))
            return BadRequest(new { message = "Enter a valid current bucket balance." });
        var targets = new List<LedgerAccountReconcileTarget>(dto.Targets.Count);
        foreach (var target in dto.Targets)
        {
            if (ReadAmount(target.ExpectedCurrent) is not { } current
                || ReadAmount(target.Target) is not { } balance
                || !TryReadOptionalAmount(target.CreditLimit, out var creditLimit, out var hasCreditLimit))
                return BadRequest(new { message = "Enter valid account amounts." });
            targets.Add(new(
                target.Id, target.Name ?? string.Empty, target.Kind, target.IsArchived, current, balance,
                target.ExpectedName, target.ExpectedKind, target.ExpectedIsArchived, creditLimit, hasCreditLimit));
        }
        var request = new LedgerAccountReconcileRequest(
            dto.OperationId ?? string.Empty,
            dto.Bucket ?? string.Empty,
            bucketTotal,
            targets,
            dto.Description,
            expectedRewardsCommitted.HasValue ? MoneyRounding.RoundMoney(expectedRewardsCommitted.Value) : null);
        var result = await _accountService.ReconcileAsync(request, HttpContext.RequestAborted);
        if (result.Status == LedgerAccountMutationStatus.Conflict)
            return Conflict(new { message = result.Message });
        if (result.Status != LedgerAccountMutationStatus.Success)
            return BadRequest(new { message = result.Message });

        var accounts = result.Accounts ?? [];
        var balances = await _accountService.GetBalancesAsync(accounts, HttpContext.RequestAborted);
        var statements = await _accountService.GetCardStatementsAsync(accounts, HttpContext.RequestAborted);
        return Ok(new
        {
            accounts = accounts.Select(account => MapToDto(account, balances, statements)).ToList(),
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
                stabilityRepaymentCycles = transaction.StabilityRepaymentCycles,
            }).ToList(),
        });
    }

    internal static LedgerAccountDto MapToDto(
        LedgerAccount account,
        IReadOnlyDictionary<string, decimal> balances,
        IReadOnlyDictionary<string, CardStatement>? statements = null)
    {
        var statement = account.StatementDay is not null && statements is not null
            && statements.TryGetValue(account.Id, out var found)
                ? found
                : null;
        return new()
        {
            Id = account.Id,
            Name = account.Name,
            Bucket = account.Bucket,
            Kind = account.Kind,
            IsArchived = account.IsArchived,
            Remaining = ObfuscationHelper.Obfuscate(
                balances.TryGetValue(account.Id, out var balance) ? balance : 0m),
            CreditLimit = account.CreditLimit is { } limit ? ObfuscationHelper.Obfuscate(limit) : null,
            StatementDay = account.StatementDay,
            // Both or neither: a client cannot work out what is due from only one of them.
            StatementDate = statement?.StatementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ChargedSinceStatement = statement is null ? null : ObfuscationHelper.Obfuscate(statement.ChargedSince),
            CreatedAt = account.CreatedAt.ToUniversalTime().ToString("O"),
            UpdatedAt = account.UpdatedAt.ToUniversalTime().ToString("O"),
        };
    }

    private static LedgerAccountMutation? ToMutation(LedgerAccountMutationDto dto, string? id = null)
    {
        var opening = dto.OpeningAmount.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? 0m
            : ReadAmount(dto.OpeningAmount);
        if (opening is null
            || !TryReadOptionalAmount(dto.CreditLimit, out var creditLimit, out var hasCreditLimit)
            || !TryReadOptionalDay(dto.StatementDay, out var statementDay, out var hasStatementDay))
            return null;
        return new(
            id ?? dto.Id ?? string.Empty,
            dto.Name ?? string.Empty,
            dto.Bucket ?? string.Empty,
            dto.Kind ?? LedgerAccountKind.Bank,
            dto.IsArchived,
            opening.Value,
            creditLimit,
            hasCreditLimit,
            statementDay,
            hasStatementDay);
    }

    // Absent keeps the stored day, null clears it, and a whole number sets it.
    private static bool TryReadOptionalDay(JsonElement value, out int? day, out bool isPresent)
    {
        isPresent = value.ValueKind != JsonValueKind.Undefined;
        day = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
        return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || day.HasValue;
    }

    // Absent keeps the stored value, null clears it, and a number sets it.
    private static bool TryReadOptionalAmount(JsonElement value, out decimal? amount, out bool isPresent)
    {
        isPresent = value.ValueKind != JsonValueKind.Undefined;
        amount = ReadAmount(value);
        return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || amount.HasValue;
    }

    private static decimal? ReadAmount(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString() ?? string.Empty;
        return ObfuscationHelper.TryDeobfuscate(text, out var obfuscated)
            ? obfuscated
            : decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var plain)
                ? plain
                : null;
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
    public JsonElement CreditLimit { get; set; }
    public JsonElement StatementDay { get; set; }
}

public sealed class LedgerAccountDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Kind { get; set; } = LedgerAccountKind.Bank;
    public bool IsArchived { get; set; }
    public string Remaining { get; set; } = string.Empty;
    public string? CreditLimit { get; set; }
    public int? StatementDay { get; set; }
    /// <summary>The latest statement close (yyyy-MM-dd), when the card records a statement day.</summary>
    public string? StatementDate { get; set; }
    /// <summary>Charged to the card after that close; what is owed less this is still due on the statement.</summary>
    public string? ChargedSinceStatement { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public sealed class LedgerAccountReconcileDto
{
    public string? OperationId { get; set; }
    public string? Bucket { get; set; }
    public JsonElement ExpectedBucketTotal { get; set; }
    public JsonElement ExpectedRewardsCommitted { get; set; }
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
    public JsonElement CreditLimit { get; set; }
}
