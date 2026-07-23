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
public sealed class InvestmentsController(
    AppDbContext context,
    InvestmentPortfolioService portfolioService,
    InvestmentAccountingService accountingService,
    InvestmentMarketDataService marketDataService) : ControllerBase
{
    [HttpGet("portfolio")]
    public async Task<ActionResult<InvestmentPortfolioDto>> GetPortfolio(
        [FromQuery] string range = "3m")
    {
        if (!new[] { "1m", "3m", "6m", "1y", "all" }.Contains(range, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { message = "Range must be 1m, 3m, 6m, 1y, or all." });
        return Ok(await portfolioService.GetPortfolioAsync(range, HttpContext.RequestAborted));
    }

    [HttpGet("accounts")]
    public async Task<ActionResult<IReadOnlyList<InvestmentAccount>>> GetAccounts()
        => Ok(await context.InvestmentAccounts.AsNoTracking()
            .OrderBy(value => value.IsArchived).ThenBy(value => value.Name)
            .ToListAsync(HttpContext.RequestAborted));

    [HttpPost("accounts")]
    public async Task<ActionResult<InvestmentAccount>> CreateAccount(AccountMutationDto dto)
    {
        var error = ValidateAccount(dto);
        if (error is not null) return BadRequest(new { message = error });
        var account = new InvestmentAccount
        {
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
        var account = await context.InvestmentAccounts.FindAsync([id], HttpContext.RequestAborted);
        if (account is null) return NotFound();
        account.Name = dto.Name.Trim();
        account.BaseCurrency = dto.BaseCurrency.Trim().ToUpperInvariant();
        account.IsArchived = dto.IsArchived;
        account.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpPost("accounts/{id:guid}/archive")]
    public async Task<IActionResult> ArchiveAccount(Guid id)
    {
        var account = await context.InvestmentAccounts.FindAsync([id], HttpContext.RequestAborted);
        if (account is null) return NotFound();
        account.IsArchived = true;
        account.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpDelete("accounts/{id:guid}")]
    public async Task<IActionResult> DeleteAccount(Guid id)
    {
        var account = await context.InvestmentAccounts.FindAsync([id], HttpContext.RequestAborted);
        if (account is null) return NotFound();
        if (await context.InvestmentTransactions.AnyAsync(value => value.AccountId == id, HttpContext.RequestAborted))
            return Conflict(new { message = "Accounts with activity cannot be deleted. Archive this account instead." });
        context.InvestmentAccounts.Remove(account);
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpGet("instruments/search")]
    public async Task<ActionResult<InvestmentSearchResponse>> SearchInstruments([FromQuery] string q = "")
        => Ok(await marketDataService.SearchAsync(q, HttpContext.RequestAborted));

    [HttpPost("instruments")]
    public async Task<ActionResult<InvestmentInstrument>> CreateInstrument(InstrumentMutationDto dto)
    {
        var error = ValidateInstrument(dto);
        if (error is not null) return BadRequest(new { message = error });
        var instrument = MapInstrument(dto);
        context.InvestmentInstruments.Add(instrument);
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
        var instrument = await context.InvestmentInstruments.FindAsync([id], HttpContext.RequestAborted);
        if (instrument is null) return NotFound();
        if (!instrument.IsCustom &&
            await context.InvestmentTransactions.AnyAsync(value => value.InstrumentId == id, HttpContext.RequestAborted) &&
            (!instrument.Symbol.Equals(dto.Symbol, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(instrument.ProviderMic, dto.ProviderMic, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(new { message = "A provider-backed instrument with activity cannot change its market mapping." });
        }
        ApplyInstrument(instrument, dto);
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpDelete("instruments/{id:guid}")]
    public async Task<IActionResult> DeleteInstrument(Guid id)
    {
        var instrument = await context.InvestmentInstruments.FindAsync([id], HttpContext.RequestAborted);
        if (instrument is null) return NotFound();
        if (await context.InvestmentTransactions.AnyAsync(value => value.InstrumentId == id, HttpContext.RequestAborted))
            return Conflict(new { message = "Instruments with activity cannot be deleted." });
        context.InvestmentInstruments.Remove(instrument);
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpPost("transactions")]
    public async Task<ActionResult<InvestmentTransactionDto>> CreateTransaction(InvestmentTransactionMutationDto dto)
    {
        var result = await BuildTransactionAsync(dto, null);
        if (result.Error is not null) return BadRequest(new { message = result.Error });
        var transaction = result.Transaction!;
        context.InvestmentTransactions.Add(transaction);
        InvestmentTransaction? transferIn = null;
        if (transaction.Type == "TransferOut" && dto.DestinationAccountId is Guid destination)
        {
            transferIn = new InvestmentTransaction
            {
                AccountId = destination,
                InstrumentId = transaction.InstrumentId,
                Type = "TransferIn",
                TradeDate = transaction.TradeDate,
                Units = transaction.Units,
                LinkedTransferId = transaction.Id,
                Notes = transaction.Notes,
                Instrument = transaction.Instrument
            };
            context.InvestmentTransactions.Add(transferIn);
        }
        var validation = await ValidateHistoryAsync(null);
        if (validation is not null) return BadRequest(new { message = validation });
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Created($"/api/investments/transactions/{transaction.Id}", InvestmentPortfolioService.ToDto(transaction));
    }

    [HttpPut("transactions/{id:guid}")]
    public async Task<IActionResult> UpdateTransaction(Guid id, InvestmentTransactionMutationDto dto)
    {
        var existing = await context.InvestmentTransactions
            .Include(value => value.Instrument)
            .FirstOrDefaultAsync(value => value.Id == id, HttpContext.RequestAborted);
        if (existing is null) return NotFound();
        if (existing.LinkedTransferId is not null ||
            await context.InvestmentTransactions.AnyAsync(value => value.LinkedTransferId == id, HttpContext.RequestAborted))
            return Conflict(new { message = "Paired internal transfers must be deleted and recreated." });
        var result = await BuildTransactionAsync(dto, existing);
        if (result.Error is not null) return BadRequest(new { message = result.Error });
        var validation = await ValidateHistoryAsync(null);
        if (validation is not null) return BadRequest(new { message = validation });
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpDelete("transactions/{id:guid}")]
    public async Task<IActionResult> DeleteTransaction(Guid id)
    {
        var transaction = await context.InvestmentTransactions
            .FirstOrDefaultAsync(value => value.Id == id, HttpContext.RequestAborted);
        if (transaction is null) return NotFound();
        var linked = await context.InvestmentTransactions
            .Where(value => value.LinkedTransferId == id || value.Id == transaction.LinkedTransferId)
            .ToListAsync(HttpContext.RequestAborted);
        context.InvestmentTransactions.RemoveRange(linked);
        context.InvestmentTransactions.Remove(transaction);
        var validation = await ValidateHistoryAsync(id);
        if (validation is not null)
        {
            context.ChangeTracker.Clear();
            return Conflict(new { message = $"This activity cannot be deleted because later activity would become invalid: {validation}" });
        }
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpPost("manual-prices")]
    public async Task<ActionResult<ManualPriceDto>> CreateManualPrice(ManualPriceMutationDto dto)
    {
        var error = await ValidateManualPriceAsync(dto);
        if (error is not null) return BadRequest(new { message = error });
        var manual = new ManualPriceOverride
        {
            InstrumentId = dto.InstrumentId,
            MarketDate = dto.MarketDate,
            Price = dto.Price,
            FxRate = dto.FxRate
        };
        context.ManualPriceOverrides.Add(manual);
        try
        {
            await context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "A manual price already exists for this instrument and date." });
        }
        return Created($"/api/investments/manual-prices/{manual.Id}", InvestmentPortfolioService.ToDto(manual));
    }

    [HttpPut("manual-prices/{id:guid}")]
    public async Task<IActionResult> UpdateManualPrice(Guid id, ManualPriceMutationDto dto)
    {
        var error = await ValidateManualPriceAsync(dto);
        if (error is not null) return BadRequest(new { message = error });
        var manual = await context.ManualPriceOverrides.FindAsync([id], HttpContext.RequestAborted);
        if (manual is null) return NotFound();
        manual.InstrumentId = dto.InstrumentId;
        manual.MarketDate = dto.MarketDate;
        manual.Price = dto.Price;
        manual.FxRate = dto.FxRate;
        manual.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpDelete("manual-prices/{id:guid}")]
    public async Task<IActionResult> DeleteManualPrice(Guid id)
    {
        var manual = await context.ManualPriceOverrides.FindAsync([id], HttpContext.RequestAborted);
        if (manual is null) return NotFound();
        context.ManualPriceOverrides.Remove(manual);
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpPost("market-data/refresh")]
    public async Task<ActionResult<MarketRefreshResponse>> RefreshMarketData()
        => Ok(await marketDataService.RefreshAsync(HttpContext.RequestAborted));

    private async Task<(InvestmentTransaction? Transaction, string? Error)> BuildTransactionAsync(
        InvestmentTransactionMutationDto dto,
        InvestmentTransaction? existing)
    {
        if (!InvestmentKinds.TransactionTypes.Contains(dto.Type))
            return (null, "Unsupported transaction type.");
        var account = await context.InvestmentAccounts.FindAsync([dto.AccountId], HttpContext.RequestAborted);
        if (account is null || account.IsArchived) return (null, "Select an active investment account.");
        var instrument = await context.InvestmentInstruments.FindAsync([dto.InstrumentId], HttpContext.RequestAborted);
        if (instrument is null || instrument.IsArchived) return (null, "Select an active investment.");
        if (dto.TradeDate == default || dto.TradeDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
            return (null, "Enter a valid trade date.");
        if (dto.Fees < 0 || dto.Taxes < 0 || dto.TradeFxRate is <= 0)
            return (null, "Fees and taxes cannot be negative, and FX rates must be positive.");
        if (dto.DestinationAccountId == dto.AccountId)
            return (null, "Transfer destination must be a different account.");
        if (dto.DestinationAccountId is Guid destination &&
            !await context.InvestmentAccounts.AnyAsync(value => value.Id == destination && !value.IsArchived, HttpContext.RequestAborted))
            return (null, "Transfer destination account was not found.");

        decimal units = dto.Units ?? 0;
        decimal? unitPrice = dto.UnitPrice;
        decimal? cash = dto.CashAmount;
        if (dto.Type is "Buy" or "Sell" or "OpeningPosition")
        {
            var supplied = new[] { dto.Units is > 0, dto.UnitPrice is > 0, dto.CashAmount is > 0 }.Count(value => value);
            if (supplied < 2) return (null, "Enter any two of units, unit price, and gross amount.");
            if (units <= 0) units = cash!.Value / unitPrice!.Value;
            else if (unitPrice is null or <= 0) unitPrice = cash!.Value / units;
            else if (cash is null or <= 0) cash = units * unitPrice.Value;
            var expected = units * unitPrice.Value;
            if (Math.Abs(expected - cash.Value) > Math.Max(0.01m, cash.Value * 0.000001m))
                return (null, "Units, unit price, and gross amount do not agree.");
        }
        else if (dto.Type is "Split" or "TransferIn" or "TransferOut")
        {
            if (units <= 0) return (null, dto.Type == "Split" ? "Enter a positive split ratio." : "Enter positive units.");
        }
        if (dto.Type == "TransferIn" && dto.LinkedTransferId is null && cash is not > 0)
            return (null, "External transfer-in requires transferred cost basis.");
        if (dto.Type == "TransferOut" && dto.DestinationAccountId is null && !string.IsNullOrWhiteSpace(dto.Notes))
        {
            // External transfers are valid; notes are simply retained.
        }

        var value = existing ?? new InvestmentTransaction();
        value.AccountId = dto.AccountId;
        value.InstrumentId = dto.InstrumentId;
        value.Instrument = instrument;
        value.Type = dto.Type;
        value.TradeDate = dto.TradeDate;
        value.Units = units;
        value.UnitPrice = unitPrice;
        value.CashAmount = cash;
        value.Fees = dto.Fees;
        value.Taxes = dto.Taxes;
        value.TradeFxRate = instrument.Currency == (await GetAppCurrencyAsync()) ? 1 : dto.TradeFxRate;
        value.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
        value.LinkedTransferId = dto.LinkedTransferId;
        value.UpdatedAt = DateTime.UtcNow;
        return (value, null);
    }

    private async Task<string?> ValidateHistoryAsync(Guid? excludedId)
    {
        var all = await context.InvestmentTransactions
            .Include(value => value.Instrument)
            .Where(value => excludedId == null || value.Id != excludedId)
            .ToListAsync(HttpContext.RequestAborted);
        all.AddRange(context.ChangeTracker.Entries<InvestmentTransaction>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .Where(value => all.All(existing => existing.Id != value.Id)));
        try
        {
            accountingService.Calculate(all, await GetAppCurrencyAsync());
            return null;
        }
        catch (InvestmentValidationException exception)
        {
            return exception.Message;
        }
    }

    private async Task<string> GetAppCurrencyAsync()
        => (await context.FinancialSettings.AsNoTracking()
            .Select(value => value.Currency)
            .FirstOrDefaultAsync(HttpContext.RequestAborted) ?? "USD").ToUpperInvariant();

    private async Task<string?> ValidateManualPriceAsync(ManualPriceMutationDto dto)
    {
        if (!await context.InvestmentInstruments.AnyAsync(value => value.Id == dto.InstrumentId, HttpContext.RequestAborted))
            return "Investment was not found.";
        if (dto.MarketDate == default || dto.MarketDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
            return "Enter a valid market date.";
        if (dto.Price <= 0 || dto.FxRate is <= 0) return "Prices and FX rates must be positive.";
        return null;
    }

    private static string? ValidateAccount(AccountMutationDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Trim().Length > 120) return "Account name is required.";
        if (!ValidCurrency(dto.BaseCurrency)) return "Use a three-letter account currency.";
        return null;
    }

    private static string? ValidateInstrument(InstrumentMutationDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Symbol) || dto.Symbol.Trim().Length > 32) return "Symbol is required.";
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Trim().Length > 200) return "Investment name is required.";
        if (!InvestmentKinds.InstrumentTypes.Contains(dto.Type)) return "Type must be Stock or ETF.";
        if (!ValidCurrency(dto.Currency)) return "Use a three-letter investment currency.";
        if (!dto.IsCustom && string.IsNullOrWhiteSpace(dto.ProviderSymbol)) return "Provider-backed investments require a provider symbol.";
        return null;
    }

    private static bool ValidCurrency(string value)
        => value.Trim().Length == 3 && value.Trim().All(char.IsLetter);

    private static InvestmentInstrument MapInstrument(InstrumentMutationDto dto)
    {
        var value = new InvestmentInstrument();
        ApplyInstrument(value, dto);
        return value;
    }

    private static void ApplyInstrument(InvestmentInstrument value, InstrumentMutationDto dto)
    {
        value.Symbol = dto.Symbol.Trim().ToUpperInvariant();
        value.Name = dto.Name.Trim();
        value.Type = dto.Type.Equals("ETF", StringComparison.OrdinalIgnoreCase) ? "ETF" : "Stock";
        value.Exchange = Clean(dto.Exchange);
        value.Mic = Clean(dto.Mic)?.ToUpperInvariant();
        value.Country = Clean(dto.Country);
        value.Currency = dto.Currency.Trim().ToUpperInvariant();
        value.ProviderSymbol = dto.IsCustom ? null : Clean(dto.ProviderSymbol)?.ToUpperInvariant();
        value.ProviderMic = dto.IsCustom ? null : Clean(dto.ProviderMic)?.ToUpperInvariant();
        value.IsCustom = dto.IsCustom;
        value.IsArchived = dto.IsArchived;
        value.UpdatedAt = DateTime.UtcNow;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AccountMutationDto(string Name, string BaseCurrency, bool IsArchived = false);

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
    bool IsArchived = false);

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
    decimal? TradeFxRate,
    string? Notes,
    Guid? LinkedTransferId,
    Guid? DestinationAccountId);

public sealed record ManualPriceMutationDto(
    Guid InstrumentId,
    DateOnly MarketDate,
    decimal Price,
    decimal? FxRate);
