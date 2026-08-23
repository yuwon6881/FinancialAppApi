using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Controllers;

public partial class InvestmentsController
{
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
        var validation = await historyValidationService.ValidateTransactionMutationAsync(
            HttpContext.RequestAborted);
        if (validation.PositionError is not null)
            return BadRequest(new { message = validation.PositionError });
        if (validation.CashError is not null)
            return BadRequest(new { message = validation.CashError });
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Created(
            $"/api/investments/transactions/{transaction.Id}",
            InvestmentPortfolioService.ToDto(transaction));
    }

    [HttpPut("transactions/{id:guid}")]
    public async Task<IActionResult> UpdateTransaction(Guid id, InvestmentTransactionMutationDto dto)
    {
        var existing = await context.InvestmentTransactions
            .Include(value => value.Instrument)
            .FirstOrDefaultAsync(value => value.Id == id, HttpContext.RequestAborted);
        if (existing is null) return NotFound();
        var result = await BuildTransactionAsync(dto, existing);
        if (result.Error is not null) return BadRequest(new { message = result.Error });
        var validation = await historyValidationService.ValidateTransactionMutationAsync(
            HttpContext.RequestAborted);
        if (validation.PositionError is not null)
            return BadRequest(new { message = validation.PositionError });
        if (validation.CashError is not null)
            return BadRequest(new { message = validation.CashError });
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpDelete("transactions/{id:guid}")]
    public async Task<ActionResult<DeletedTransactionsSnapshot>> DeleteTransaction(Guid id)
    {
        var transaction = await context.InvestmentTransactions
            .FirstOrDefaultAsync(value => value.Id == id, HttpContext.RequestAborted);
        if (transaction is null) return NotFound();
        context.InvestmentTransactions.Remove(transaction);
        var validation = await historyValidationService.ValidateTransactionMutationAsync(HttpContext.RequestAborted);
        var validationError = validation.PositionError ?? validation.CashError;
        if (validationError is not null)
        {
            context.ChangeTracker.Clear();
            return Conflict(new { message = $"This activity cannot be deleted because later activity would become invalid: {validationError}" });
        }
        var snapshot = new DeletedTransactionsSnapshot(
            [InvestmentPortfolioService.ToDto(transaction)]);
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(snapshot);
    }

    [HttpPost("transactions/restore")]
    public async Task<IActionResult> RestoreTransactions(DeletedTransactionsSnapshot snapshot)
    {
        if (snapshot.Transactions.Count != 1)
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
                Type = InvestmentKinds.CanonicalTransactionType(item.Type)!,
                TradeDate = item.TradeDate,
                Units = item.Units,
                UnitPrice = item.UnitPrice,
                CashAmount = item.CashAmount,
                Fees = item.Fees,
                Taxes = item.Taxes,
                CreatedAt = item.CreatedAt
            };
            restored.Add(transaction);
            context.InvestmentTransactions.Add(transaction);
        }
        var validation = await historyValidationService.ValidateTransactionMutationAsync(HttpContext.RequestAborted);
        var historyError = validation.PositionError ?? validation.CashError;
        if (historyError is not null)
        {
            foreach (var transaction in restored) context.Entry(transaction).State = EntityState.Detached;
            return Conflict(new { message = $"This activity can no longer be restored: {historyError}" });
        }
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
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
        var validation = await historyValidationService.ValidateCashHistoryAsync(HttpContext.RequestAborted);
        if (validation is not null) return BadRequest(new { message = validation });
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
        var validation = await historyValidationService.ValidateCashHistoryAsync(HttpContext.RequestAborted);
        if (validation is not null) return BadRequest(new { message = validation });
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(InvestmentPortfolioService.ToDto(flow!));
    }

    [HttpDelete("cash-flows/{id:guid}")]
    public async Task<ActionResult<InvestmentCashFlowDto>> DeleteCashFlow(Guid id)
    {
        var flow = await context.InvestmentCashFlows
            .SingleOrDefaultAsync(value => value.Id == id, HttpContext.RequestAborted);
        if (flow is null) return NotFound();
        var snapshot = InvestmentPortfolioService.ToDto(flow);
        context.InvestmentCashFlows.Remove(flow);
        var validation = await historyValidationService.ValidateCashHistoryAsync(HttpContext.RequestAborted);
        if (validation is not null)
        {
            context.ChangeTracker.Clear();
            return Conflict(new { message = $"This cash movement cannot be deleted because later activity would become invalid: {validation}" });
        }
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
                snapshot.ToAmount is not > 0)) ||
            (!restoringConversion && (
                snapshot.ToCurrency is not null ||
                snapshot.ToAmount is not null)) ||
            snapshot.Date == default ||
            snapshot.Date > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)) ||
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
            Date = snapshot.Date
        });
        var validation = await historyValidationService.ValidateCashHistoryAsync(HttpContext.RequestAborted);
        if (validation is not null) return Conflict(new { message = validation });
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    // The provider quota is a globally shared pool, so the freshness gate is applied to every
    // user-initiated refresh. It used to hang off a client-supplied `automatic` query flag,
    // which let any authenticated caller loop this endpoint and drain the ceiling for everyone.
    [HttpPost("market-data/refresh")]
    public async Task<ActionResult<MarketRefreshResponse>> RefreshMarketData()
        => Ok(await marketDataService.RefreshAsync(HttpContext.RequestAborted));
}
