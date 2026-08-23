using FinancialAppApi.Database;
using FinancialAppApi.Filters;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Controllers;

[ApiController]
[AuthorizeToken]
[Route("api/investments")]
public sealed partial class InvestmentsController(
    AppDbContext context,
    InvestmentPortfolioService portfolioService,
    InvestmentAccountingService accountingService,
    InvestmentHistoryValidationService historyValidationService,
    InstrumentHistoryService instrumentHistoryService,
    InvestmentQueryService queryService,
    InvestmentMarketDataService marketDataService,
    IMarketDataProvider marketDataProvider) : ControllerBase
{
    [HttpGet("portfolio")]
    public async Task<ActionResult<InvestmentPortfolioDto>> GetPortfolio(
        [FromQuery] string range = "3m")
    {
        if (!InvestmentChartRange.IsAllowed(range))
            return BadRequest(new { message = $"Range must be one of {string.Join(", ", InvestmentChartRange.Allowed)}." });
        return Ok(await portfolioService.GetPortfolioAsync(range, HttpContext.RequestAborted));
    }

    [HttpGet("instruments/{id:guid}/history")]
    public async Task<ActionResult<InstrumentHistoryDto>> GetInstrumentHistory(
        Guid id,
        [FromQuery] string range = "1y")
    {
        if (!InvestmentChartRange.IsAllowed(range))
            return BadRequest(new { message = $"Range must be one of {string.Join(", ", InvestmentChartRange.Allowed)}." });
        var history = await instrumentHistoryService.GetAsync(id, range, HttpContext.RequestAborted);
        return history is null ? NotFound() : Ok(history);
    }

    [HttpGet("accounts")]
    public async Task<ActionResult<IReadOnlyList<InvestmentAccount>>> GetAccounts()
        => Ok(await queryService.GetAccountsAsync(HttpContext.RequestAborted));

    [HttpGet("currencies")]
    public ActionResult<IReadOnlyList<CurrencyCatalogItem>> GetCurrencies()
        => Ok(CurrencyCatalog.Items);

    [HttpGet("transactions")]
    public async Task<ActionResult<PagedResult<InvestmentTransactionDto>>> GetTransactions(
        [FromQuery] Guid? accountId,
        [FromQuery] Guid? instrumentId,
        [FromQuery] string? type,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        if (!ValidPage(page, pageSize)) return BadRequest(new { message = "Page must be positive and page size must be 10, 25, or 50." });
        return Ok(await queryService.GetTransactionsAsync(
            accountId, instrumentId, type, from, to, page, pageSize, HttpContext.RequestAborted));
    }

    [HttpGet("cash-flows")]
    public async Task<ActionResult<PagedResult<InvestmentCashFlowDto>>> GetCashFlows(
        [FromQuery] Guid? accountId,
        [FromQuery] string? type,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        if (!ValidPage(page, pageSize)) return BadRequest(new { message = "Page must be positive and page size must be 10, 25, or 50." });
        return Ok(await queryService.GetCashFlowsAsync(
            accountId, type, from, to, page, pageSize, HttpContext.RequestAborted));
    }

    [HttpPost("accounts")]
    public async Task<ActionResult<InvestmentAccount>> CreateAccount(AccountMutationDto dto)
    {
        var error = ValidateAccount(dto);
        if (error is not null) return BadRequest(new { message = error });
        if (dto.Id is Guid requestedId)
        {
            var existing = await context.InvestmentAccounts.AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == requestedId, HttpContext.RequestAborted);
            if (existing is not null) return Ok(existing);
        }
        var account = new InvestmentAccount
        {
            Id = dto.Id ?? Guid.NewGuid(),
            Name = dto.Name.Trim(),
            BaseCurrency = dto.BaseCurrency.Trim().ToUpperInvariant()
        };
        context.InvestmentAccounts.Add(account);
        try
        {
            await context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "An investment account with this name already exists." });
        }
        return Created($"/api/investments/accounts/{account.Id}", account);
    }

    [HttpPut("accounts/{id:guid}")]
    public async Task<IActionResult> UpdateAccount(Guid id, AccountMutationDto dto)
    {
        var error = ValidateAccount(dto);
        if (error is not null) return BadRequest(new { message = error });
        var account = await context.InvestmentAccounts
            .SingleOrDefaultAsync(value => value.Id == id, HttpContext.RequestAborted);
        if (account is null) return NotFound();
        if (dto.IsArchived && !account.IsArchived)
        {
            var reason = await AccountArchiveUnavailableReason(id);
            if (reason is not null) return Conflict(new { message = reason });
        }
        account.Name = dto.Name.Trim();
        account.BaseCurrency = dto.BaseCurrency.Trim().ToUpperInvariant();
        account.IsArchived = dto.IsArchived;
        account.UpdatedAt = DateTime.UtcNow;
        try
        {
            await context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "An investment account with this name already exists." });
        }
        return NoContent();
    }

    [HttpDelete("accounts/{id:guid}")]
    public async Task<ActionResult<InvestmentAccount>> DeleteAccount(Guid id)
    {
        var account = await context.InvestmentAccounts
            .SingleOrDefaultAsync(value => value.Id == id, HttpContext.RequestAborted);
        if (account is null) return NotFound();
        if (await context.InvestmentTransactions.AnyAsync(value => value.AccountId == id, HttpContext.RequestAborted) ||
            await context.InvestmentCashFlows.AnyAsync(value => value.AccountId == id, HttpContext.RequestAborted))
            return Conflict(new { message = "Accounts with activity cannot be deleted. Archive this account instead." });
        context.InvestmentAccounts.Remove(account);
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(account);
    }

    [HttpGet("instruments/search")]
    public async Task<ActionResult<InvestmentSearchResponse>> SearchInstruments([FromQuery] string q = "")
        => Ok(await marketDataService.SearchAsync(q, HttpContext.RequestAborted));

    [HttpPost("instruments")]
    public async Task<ActionResult<InvestmentInstrument>> CreateInstrument(InstrumentMutationDto dto)
    {
        var error = ValidateInstrument(dto);
        if (error is not null) return BadRequest(new { message = error });
        if (dto.Id is Guid requestedId)
        {
            var existing = await context.InvestmentInstruments.AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == requestedId, HttpContext.RequestAborted);
            if (existing is not null) return Ok(existing);
        }
        var instrument = MapInstrument(dto);
        instrument.Id = dto.Id ?? Guid.NewGuid();
        instrument.AllocationOrder = (await context.InvestmentInstruments
            .Select(value => (int?)value.AllocationOrder)
            .MaxAsync(HttpContext.RequestAborted) ?? -1) + 1;
        context.InvestmentInstruments.Add(instrument);
        await AddOrUpdateMarketMappingAsync(instrument, dto);
        try
        {
            await context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "This instrument is already saved." });
        }
        return Created($"/api/investments/instruments/{instrument.Id}", instrument);
    }

    [HttpPut("instruments/{id:guid}")]
    public async Task<IActionResult> UpdateInstrument(Guid id, InstrumentMutationDto dto)
    {
        var error = ValidateInstrument(dto);
        if (error is not null) return BadRequest(new { message = error });
        var instrument = await context.InvestmentInstruments
            .SingleOrDefaultAsync(value => value.Id == id, HttpContext.RequestAborted);
        if (instrument is null) return NotFound();
        if (dto.IsArchived && !instrument.IsArchived)
        {
            var history = await context.InvestmentTransactions.AsNoTracking()
                .Include(value => value.Instrument)
                .Where(value => value.InstrumentId == id)
                .ToListAsync(HttpContext.RequestAborted);
            if (history.Count > 0 &&
                accountingService.Calculate(history, await GetAppCurrencyAsync())
                    .Positions.Any(value => value.Units != 0))
                return Conflict(new { message = "Close all units before archiving this investment." });
        }
        var hasActivity = await context.InvestmentTransactions
            .AnyAsync(value => value.InstrumentId == id, HttpContext.RequestAborted);
        if (hasActivity && (HasHistoricalMarketIdentityChange(instrument, dto) ||
                            await HasHistoricalProviderReferenceChange(instrument.Id, dto)))
        {
            return Conflict(new
            {
                message = "An investment with activity cannot change its currency or market-data mapping. Create a new investment instead."
            });
        }
        ApplyInstrument(instrument, dto);
        await AddOrUpdateMarketMappingAsync(instrument, dto);
        try
        {
            await context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "This instrument is already saved." });
        }
        return NoContent();
    }

    [HttpDelete("instruments/{id:guid}")]
    public async Task<ActionResult<InvestmentInstrument>> DeleteInstrument(Guid id)
    {
        var instrument = await context.InvestmentInstruments
            .SingleOrDefaultAsync(value => value.Id == id, HttpContext.RequestAborted);
        if (instrument is null) return NotFound();
        if (await context.InvestmentTransactions.AnyAsync(value => value.InstrumentId == id, HttpContext.RequestAborted))
            return Conflict(new { message = "Investments with activity cannot be deleted. Archive this investment after closing all units." });
        context.InvestmentInstruments.Remove(instrument);
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(instrument);
    }

}

public sealed record AccountMutationDto(string Name, string BaseCurrency, bool IsArchived = false, Guid? Id = null);

public sealed record InstrumentMutationDto(
    string Symbol,
    string Name,
    string Type,
    string Currency,
    string? Exchange,
    string? Mic,
    string? Country,
    string? ProviderSymbol,
    string? ProviderMic,
    bool IsCustom,
    bool IsArchived = false,
    Guid? Id = null,
    MarketInstrumentReference? MarketDataReference = null);

public sealed record InvestmentTransactionMutationDto(
    Guid AccountId,
    Guid InstrumentId,
    string Type,
    DateOnly TradeDate,
    decimal? Units,
    decimal? UnitPrice,
    decimal? CashAmount,
    decimal Fees,
    decimal Taxes,
    Guid? Id = null,
    DateTime? CreatedAt = null);

public sealed record CashFlowMutationDto(
    Guid AccountId,
    string Currency,
    string Type,
    decimal Amount,
    DateOnly Date,
    Guid? Id = null,
    string? ToCurrency = null,
    decimal? ToAmount = null,
    DateTime? CreatedAt = null);

public sealed record DeletedTransactionsSnapshot(IReadOnlyList<InvestmentTransactionDto> Transactions);
