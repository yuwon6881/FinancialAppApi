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
        var query = context.InvestmentTransactions.AsNoTracking();
        if (accountId is not null) query = query.Where(value => value.AccountId == accountId);
        if (instrumentId is not null) query = query.Where(value => value.InstrumentId == instrumentId);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(value => value.Type == type);
        if (from is not null) query = query.Where(value => value.TradeDate >= from);
        if (to is not null) query = query.Where(value => value.TradeDate <= to);
        var total = await query.CountAsync(HttpContext.RequestAborted);
        var rows = await query.OrderByDescending(value => value.TradeDate)
            .ThenByDescending(value => value.CreatedAt)
            .ThenByDescending(value => value.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(HttpContext.RequestAborted);
        var rowIds = rows.Select(value => value.Id).ToList();
        var pairedOutgoingIds = await context.InvestmentTransactions.AsNoTracking()
            .Where(value => value.LinkedTransferId != null && rowIds.Contains(value.LinkedTransferId.Value))
            .Select(value => value.LinkedTransferId!.Value)
            .ToHashSetAsync(HttpContext.RequestAborted);
        var items = rows.Select(value =>
            InvestmentPortfolioService.ToDto(value, pairedOutgoingIds.Contains(value.Id))).ToList();
        return Ok(new PagedResult<InvestmentTransactionDto>(items, total, page, pageSize));
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
        var query = context.InvestmentCashFlows.AsNoTracking();
        if (accountId is not null) query = query.Where(value => value.AccountId == accountId);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(value => value.Type == type);
        if (from is not null) query = query.Where(value => value.Date >= from);
        if (to is not null) query = query.Where(value => value.Date <= to);
        var total = await query.CountAsync(HttpContext.RequestAborted);
        var rows = await query.OrderByDescending(value => value.Date)
            .ThenByDescending(value => value.CreatedAt)
            .ThenByDescending(value => value.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(HttpContext.RequestAborted);
        var items = rows.Select(InvestmentPortfolioService.ToDto).ToList();
        return Ok(new PagedResult<InvestmentCashFlowDto>(items, total, page, pageSize));
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
        var account = await context.InvestmentAccounts.FindAsync([id], HttpContext.RequestAborted);
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

    [HttpPost("accounts/{id:guid}/archive")]
    public async Task<IActionResult> ArchiveAccount(Guid id)
    {
        var account = await context.InvestmentAccounts.FindAsync([id], HttpContext.RequestAborted);
        if (account is null) return NotFound();
        var reason = await AccountArchiveUnavailableReason(id);
        if (reason is not null) return Conflict(new { message = reason });
        account.IsArchived = true;
        account.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpDelete("accounts/{id:guid}")]
    public async Task<ActionResult<InvestmentAccount>> DeleteAccount(Guid id)
    {
        var account = await context.InvestmentAccounts.FindAsync([id], HttpContext.RequestAborted);
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
        if (hasActivity && HasHistoricalMarketIdentityChange(instrument, dto))
        {
            return Conflict(new
            {
                message = "An investment with activity cannot change its currency or market-data mapping. Create a new investment instead."
            });
        }
        ApplyInstrument(instrument, dto);
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
        var instrument = await context.InvestmentInstruments.FindAsync([id], HttpContext.RequestAborted);
        if (instrument is null) return NotFound();
        if (await context.InvestmentTransactions.AnyAsync(value => value.InstrumentId == id, HttpContext.RequestAborted) ||
            await context.ManualPriceOverrides.AnyAsync(value => value.InstrumentId == id, HttpContext.RequestAborted))
            return Conflict(new { message = "Investments with activity or manual prices cannot be deleted. Archive this investment after closing all units." });
        context.InvestmentInstruments.Remove(instrument);
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(instrument);
    }

    [HttpPost("transactions")]
    public async Task<ActionResult<InvestmentTransactionDto>> CreateTransaction(InvestmentTransactionMutationDto dto)
    {
        if (dto.Id is Guid requestedId)
        {
            var existing = await context.InvestmentTransactions.AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == requestedId, HttpContext.RequestAborted);
            if (existing is not null) return Ok(InvestmentPortfolioService.ToDto(existing));
        }
        var result = await BuildTransactionAsync(dto, null);
        if (result.Error is not null) return BadRequest(new { message = result.Error });
        var transaction = result.Transaction!;
        transaction.Id = dto.Id ?? Guid.NewGuid();
        context.InvestmentTransactions.Add(transaction);
        InvestmentTransaction? transferIn = null;
        if (transaction.Type == "TransferOut" && dto.DestinationAccountId is Guid destination)
        {
            transferIn = new InvestmentTransaction
            {
                Id = dto.DestinationLegId ?? Guid.NewGuid(),
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
        return Created(
            $"/api/investments/transactions/{transaction.Id}",
            InvestmentPortfolioService.ToDto(transaction, transferIn is not null));
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
    public async Task<ActionResult<DeletedTransactionsSnapshot>> DeleteTransaction(Guid id)
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
        var snapshot = new DeletedTransactionsSnapshot(
            linked.Append(transaction).DistinctBy(value => value.Id)
                .Select(value => InvestmentPortfolioService.ToDto(value)).ToList());
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(snapshot);
    }

    [HttpPost("transactions/restore")]
    public async Task<IActionResult> RestoreTransactions(DeletedTransactionsSnapshot snapshot)
    {
        if (snapshot.Transactions.Count is < 1 or > 2)
            return BadRequest(new { message = "The activity snapshot is invalid." });
        var snapshotError = ValidateTransactionSnapshot(snapshot.Transactions);
        if (snapshotError is not null)
            return BadRequest(new { message = snapshotError });
        var accountIds = snapshot.Transactions.Select(value => value.AccountId).Distinct().ToList();
        var instrumentIds = snapshot.Transactions.Select(value => value.InstrumentId).Distinct().ToList();
        var restoredInstruments = await context.InvestmentInstruments
            .Where(value => instrumentIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, HttpContext.RequestAborted);
        if (await context.InvestmentAccounts.CountAsync(value => accountIds.Contains(value.Id), HttpContext.RequestAborted) != accountIds.Count ||
            restoredInstruments.Count != instrumentIds.Count)
            return Conflict(new { message = "The account or investment required by this activity no longer exists." });
        var snapshotIds = snapshot.Transactions.Select(item => item.Id).ToList();
        var existingCount = await context.InvestmentTransactions.CountAsync(value =>
            snapshotIds.Contains(value.Id), HttpContext.RequestAborted);
        if (existingCount == snapshotIds.Count)
            return NoContent();
        if (existingCount > 0)
            return Conflict(new { message = "Only part of this activity already exists; refresh before restoring it." });

        var restored = new List<InvestmentTransaction>();
        foreach (var item in snapshot.Transactions)
        {
            var transaction = new InvestmentTransaction
            {
                Id = item.Id,
                AccountId = item.AccountId,
                InstrumentId = item.InstrumentId,
                Instrument = restoredInstruments[item.InstrumentId],
                Type = item.Type,
                TradeDate = item.TradeDate,
                Units = item.Units,
                UnitPrice = item.UnitPrice,
                CashAmount = item.CashAmount,
                Fees = item.Fees,
                Taxes = item.Taxes,
                TradeFxRate = item.TradeFxRate,
                Notes = item.Notes,
                LinkedTransferId = item.LinkedTransferId,
                CreatedAt = item.CreatedAt
            };
            restored.Add(transaction);
            context.InvestmentTransactions.Add(transaction);
        }
        var historyError = await ValidateHistoryAsync(null);
        if (historyError is not null)
        {
            foreach (var transaction in restored) context.Entry(transaction).State = EntityState.Detached;
            return Conflict(new { message = $"This activity can no longer be restored: {historyError}" });
        }
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpPost("manual-prices")]
    public async Task<ActionResult<ManualPriceDto>> CreateManualPrice(ManualPriceMutationDto dto)
    {
        var error = await ValidateManualPriceAsync(dto);
        if (error is not null) return BadRequest(new { message = error });
        if (dto.Id is Guid requestedId)
        {
            var existing = await context.ManualPriceOverrides.AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == requestedId, HttpContext.RequestAborted);
            if (existing is not null) return Ok(InvestmentPortfolioService.ToDto(existing));
        }
        var manual = new ManualPriceOverride
        {
            Id = dto.Id ?? Guid.NewGuid(),
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
        try
        {
            await context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "A manual price already exists for this instrument and date." });
        }
        return NoContent();
    }

    [HttpDelete("manual-prices/{id:guid}")]
    public async Task<ActionResult<ManualPriceDto>> DeleteManualPrice(Guid id)
    {
        var manual = await context.ManualPriceOverrides.FindAsync([id], HttpContext.RequestAborted);
        if (manual is null) return NotFound();
        var snapshot = InvestmentPortfolioService.ToDto(manual);
        context.ManualPriceOverrides.Remove(manual);
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(snapshot);
    }

    [HttpPost("cash-flows")]
    public async Task<ActionResult<InvestmentCashFlowDto>> CreateCashFlow(CashFlowMutationDto dto)
    {
        if (dto.Id is Guid requestedId)
        {
            var existing = await context.InvestmentCashFlows.AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == requestedId, HttpContext.RequestAborted);
            if (existing is not null) return Ok(InvestmentPortfolioService.ToDto(existing));
        }
        var (flow, error) = await BuildCashFlowAsync(dto);
        if (error is not null) return BadRequest(new { message = error });
        context.InvestmentCashFlows.Add(flow!);
        flow!.Id = dto.Id ?? Guid.NewGuid();
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Created($"/api/investments/cash-flows/{flow!.Id}", InvestmentPortfolioService.ToDto(flow));
    }

    [HttpPut("cash-flows/{id:guid}")]
    public async Task<ActionResult<InvestmentCashFlowDto>> UpdateCashFlow(Guid id, CashFlowMutationDto dto)
    {
        var existing = await context.InvestmentCashFlows
            .FirstOrDefaultAsync(value => value.Id == id, HttpContext.RequestAborted);
        if (existing is null) return NotFound();
        var (flow, error) = await BuildCashFlowAsync(dto, existing);
        if (error is not null) return BadRequest(new { message = error });
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(InvestmentPortfolioService.ToDto(flow!));
    }

    [HttpDelete("cash-flows/{id:guid}")]
    public async Task<ActionResult<InvestmentCashFlowDto>> DeleteCashFlow(Guid id)
    {
        var flow = await context.InvestmentCashFlows.FindAsync([id], HttpContext.RequestAborted);
        if (flow is null) return NotFound();
        var snapshot = InvestmentPortfolioService.ToDto(flow);
        context.InvestmentCashFlows.Remove(flow);
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(snapshot);
    }

    [HttpPost("cash-flows/restore")]
    public async Task<IActionResult> RestoreCashFlow(InvestmentCashFlowDto snapshot)
    {
        var restoringConversion = snapshot.Type.Equals("Conversion", StringComparison.OrdinalIgnoreCase);
        if (!CurrencyCatalog.Contains(snapshot.Currency) ||
            !InvestmentKinds.CashFlowTypes.Contains(snapshot.Type) ||
            (snapshot.Type.Equals("Deposit", StringComparison.OrdinalIgnoreCase) && snapshot.Amount <= 0) ||
            (snapshot.Type.Equals("Withdrawal", StringComparison.OrdinalIgnoreCase) && snapshot.Amount >= 0) ||
            (restoringConversion && (
                snapshot.Amount >= 0 ||
                snapshot.ToCurrency is null ||
                !CurrencyCatalog.Contains(snapshot.ToCurrency) ||
                snapshot.ToCurrency.Equals(snapshot.Currency, StringComparison.OrdinalIgnoreCase) ||
                snapshot.ToAmount is not > 0 ||
                snapshot.FxRate is not > 0)) ||
            (!restoringConversion && (
                snapshot.ToCurrency is not null ||
                snapshot.ToAmount is not null ||
                snapshot.FxRate is not null)) ||
            snapshot.Date == default ||
            snapshot.Date > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)) ||
            snapshot.Notes?.Length > 1000 ||
            !await context.InvestmentAccounts.AnyAsync(value => value.Id == snapshot.AccountId, HttpContext.RequestAborted))
            return BadRequest(new { message = "The cash-flow snapshot is invalid." });
        if (await context.InvestmentCashFlows.AnyAsync(value => value.Id == snapshot.Id, HttpContext.RequestAborted))
            return NoContent();
        context.InvestmentCashFlows.Add(new InvestmentCashFlow
        {
            Id = snapshot.Id,
            AccountId = snapshot.AccountId,
            Currency = snapshot.Currency.ToUpperInvariant(),
            Type = restoringConversion
                ? "Conversion"
                : snapshot.Type.Equals("Withdrawal", StringComparison.OrdinalIgnoreCase)
                    ? "Withdrawal"
                    : "Deposit",
            Amount = snapshot.Amount,
            ToCurrency = snapshot.ToCurrency?.ToUpperInvariant(),
            ToAmount = snapshot.ToAmount,
            FxRate = snapshot.FxRate,
            Date = snapshot.Date,
            Notes = snapshot.Notes
        });
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpPost("market-data/refresh")]
    public async Task<ActionResult<MarketRefreshResponse>> RefreshMarketData(
        [FromQuery] bool automatic = false)
        => Ok(await marketDataService.RefreshAsync(automatic, HttpContext.RequestAborted));

    private async Task<(InvestmentCashFlow? Flow, string? Error)> BuildCashFlowAsync(
        CashFlowMutationDto dto,
        InvestmentCashFlow? existing = null)
    {
        if (!InvestmentKinds.CashFlowTypes.Contains(dto.Type))
            return (null, "Cash flow type must be Deposit, Withdrawal, or Conversion.");
        var account = await context.InvestmentAccounts.FindAsync([dto.AccountId], HttpContext.RequestAborted);
        if (account is null || account.IsArchived) return (null, "Select an active investment account.");
        if (!ValidCurrency(dto.Currency)) return (null, "Select a supported currency from the list.");
        if (dto.Amount <= 0) return (null, "Enter a positive amount.");
        if (dto.Notes?.Length > 1000) return (null, "Notes cannot exceed 1000 characters.");
        if (dto.Date == default || dto.Date > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
            return (null, "Enter a valid date.");

        var isConversion = dto.Type.Equals("Conversion", StringComparison.OrdinalIgnoreCase);
        var type = isConversion
            ? "Conversion"
            : dto.Type.Equals("Withdrawal", StringComparison.OrdinalIgnoreCase) ? "Withdrawal" : "Deposit";
        var from = dto.Currency.Trim().ToUpperInvariant();
        string? toCurrency = null;
        decimal? toAmount = null;
        decimal? fxRate = null;
        // Store the net effect signed: withdrawals and the sold leg of a
        // conversion reduce the balance.
        var signed = type is "Deposit" ? dto.Amount : -dto.Amount;

        if (isConversion)
        {
            if (string.IsNullOrWhiteSpace(dto.ToCurrency) || !ValidCurrency(dto.ToCurrency))
                return (null, "Select a supported currency to convert into.");
            toCurrency = dto.ToCurrency.Trim().ToUpperInvariant();
            if (toCurrency == from)
                return (null, "A conversion must use two different currencies.");
            if (dto.ToAmount is not > 0)
                return (null, "Enter a positive converted amount.");
            toAmount = dto.ToAmount.Value;
            var derived = toAmount.Value / dto.Amount;
            if (dto.FxRate is not null)
            {
                if (dto.FxRate is not > 0) return (null, "Enter a positive FX rate.");
                // Never store a rate that contradicts the two amounts.
                if (Math.Abs(dto.FxRate.Value - derived) > derived * 0.001m)
                    return (null, "The FX rate does not match the amounts entered.");
            }
            fxRate = dto.FxRate ?? derived;
        }

        var flow = existing ?? new InvestmentCashFlow();
        flow.AccountId = dto.AccountId;
        flow.Currency = from;
        flow.Type = type;
        flow.Amount = signed;
        flow.ToCurrency = toCurrency;
        flow.ToAmount = toAmount;
        flow.FxRate = fxRate;
        flow.Date = dto.Date;
        flow.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
        if (existing is not null) flow.UpdatedAt = DateTime.UtcNow;
        return (flow, null);
    }

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
        if (dto.Notes?.Length > 1000)
            return (null, "Notes cannot exceed 1000 characters.");
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
        if (dto.Type == "Dividend" && cash is not > 0)
            return (null, "Enter a positive gross dividend.");
        if (dto.Type == "FeeTax" && (cash ?? 0) + dto.Fees + dto.Taxes <= 0)
            return (null, "Enter a positive fee or tax charge.");
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

    internal static string? ValidateTransactionSnapshot(IReadOnlyList<InvestmentTransactionDto> items)
    {
        if (items.Any(item =>
                !InvestmentKinds.TransactionTypes.Contains(item.Type) ||
                item.TradeDate == default ||
                item.TradeDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)) ||
                item.Fees < 0 ||
                item.Taxes < 0 ||
                item.TradeFxRate is <= 0 ||
                item.Notes?.Length > 1000))
        {
            return "The activity snapshot contains invalid values.";
        }

        if (items.Count == 1)
        {
            var item = items[0];
            if (item.Type == "TransferIn" && item.LinkedTransferId is not null)
                return "A linked transfer must restore both activity legs.";
            return null;
        }

        var outgoing = items.SingleOrDefault(item => item.Type == "TransferOut");
        var incoming = items.SingleOrDefault(item => item.Type == "TransferIn");
        if (outgoing is null || incoming is null ||
            incoming.LinkedTransferId != outgoing.Id ||
            outgoing.LinkedTransferId is not null ||
            outgoing.AccountId == incoming.AccountId ||
            outgoing.InstrumentId != incoming.InstrumentId ||
            outgoing.TradeDate != incoming.TradeDate ||
            outgoing.Units != incoming.Units)
        {
            return "The linked transfer snapshot is inconsistent.";
        }

        return null;
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
        if (!await context.InvestmentInstruments.AnyAsync(
                value => value.Id == dto.InstrumentId && !value.IsArchived,
                HttpContext.RequestAborted))
            return "Select an active investment.";
        if (dto.MarketDate == default || dto.MarketDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
            return "Enter a valid market date.";
        if (dto.Price <= 0 || dto.FxRate is <= 0) return "Prices and FX rates must be positive.";
        return null;
    }

    private static string? ValidateAccount(AccountMutationDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Trim().Length > 120) return "Account name is required.";
        if (!ValidCurrency(dto.BaseCurrency)) return "Select a supported currency from the list.";
        return null;
    }

    private static string? ValidateInstrument(InstrumentMutationDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Symbol) || dto.Symbol.Trim().Length > 32) return "Symbol is required.";
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Trim().Length > 200) return "Investment name is required.";
        if (!InvestmentKinds.InstrumentTypes.Contains(dto.Type)) return "Type must be Stock, ETF, or Mutual Fund.";
        if (!ValidCurrency(dto.Currency)) return "Select a supported currency from the list.";
        if (dto.Exchange?.Trim().Length > 120 || dto.Mic?.Trim().Length > 8 ||
            dto.Country?.Trim().Length > 80 || dto.ProviderSymbol?.Trim().Length > 32 ||
            dto.ProviderMic?.Trim().Length > 8)
            return "Investment market details are too long.";
        if (!dto.IsCustom && string.IsNullOrWhiteSpace(dto.ProviderSymbol)) return "Provider-backed investments require a provider symbol.";
        return null;
    }

    internal static bool HasHistoricalMarketIdentityChange(
        InvestmentInstrument existing,
        InstrumentMutationDto updated)
    {
        var providerSymbol = updated.IsCustom ? null : Clean(updated.ProviderSymbol)?.ToUpperInvariant();
        var providerMic = updated.IsCustom ? null : Clean(updated.ProviderMic)?.ToUpperInvariant();
        return !existing.Currency.Equals(updated.Currency.Trim(), StringComparison.OrdinalIgnoreCase) ||
               existing.IsCustom != updated.IsCustom ||
               !string.Equals(existing.ProviderSymbol, providerSymbol, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(existing.ProviderMic, providerMic, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidCurrency(string value)
        => CurrencyCatalog.Contains(value);

    private static bool ValidPage(int page, int pageSize)
        => page > 0 && pageSize is 10 or 25 or 50;

    private async Task<string?> AccountArchiveUnavailableReason(Guid accountId)
    {
        var transactions = await context.InvestmentTransactions.AsNoTracking()
            .Include(value => value.Instrument)
            .Where(value => value.AccountId == accountId)
            .ToListAsync(HttpContext.RequestAborted);
        if (transactions.Count > 0)
        {
            var calculation = accountingService.Calculate(transactions, await GetAppCurrencyAsync());
            if (calculation.Positions.Any(value => value.Units != 0))
                return "Close all positions before archiving this account.";
        }
        var flows = await context.InvestmentCashFlows.AsNoTracking()
            .Where(value => value.AccountId == accountId).ToListAsync(HttpContext.RequestAborted);
        var cash = new Dictionary<string, decimal>();
        foreach (var flow in flows)
        {
            cash[flow.Currency] = cash.GetValueOrDefault(flow.Currency) + flow.Amount;
            // A conversion's credited leg is a separate currency bucket.
            if (flow.ToCurrency is not null && flow.ToAmount is not null)
                cash[flow.ToCurrency] = cash.GetValueOrDefault(flow.ToCurrency) + flow.ToAmount.Value;
        }
        foreach (var transaction in transactions)
        {
            var currency = transaction.Instrument.Currency;
            var effect = transaction.Type switch
            {
                "Buy" => -((transaction.CashAmount ?? 0) + transaction.Fees + transaction.Taxes),
                "Sell" or "Dividend" => (transaction.CashAmount ?? 0) - transaction.Fees - transaction.Taxes,
                "FeeTax" => -((transaction.CashAmount ?? 0) + transaction.Fees + transaction.Taxes),
                _ => 0
            };
            cash[currency] = cash.GetValueOrDefault(currency) + effect;
        }
        return cash.Values.Any(value => value != 0)
            ? "Bring every cash balance to zero before archiving this account."
            : null;
    }

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
        value.Type = dto.Type.Equals("ETF", StringComparison.OrdinalIgnoreCase)
            ? "ETF"
            : dto.Type.Equals("MutualFund", StringComparison.OrdinalIgnoreCase) ? "MutualFund" : "Stock";
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
    Guid? Id = null);

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
    Guid? DestinationAccountId,
    Guid? Id = null,
    Guid? DestinationLegId = null);

public sealed record ManualPriceMutationDto(
    Guid InstrumentId,
    DateOnly MarketDate,
    decimal Price,
    decimal? FxRate,
    Guid? Id = null);

public sealed record CashFlowMutationDto(
    Guid AccountId,
    string Currency,
    string Type,
    decimal Amount,
    DateOnly Date,
    string? Notes,
    Guid? Id = null,
    string? ToCurrency = null,
    decimal? ToAmount = null,
    decimal? FxRate = null);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record DeletedTransactionsSnapshot(IReadOnlyList<InvestmentTransactionDto> Transactions);
