using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed class InvestmentQueryService(AppDbContext context)
{
    public Task<List<InvestmentAccount>> GetAccountsAsync(CancellationToken cancellationToken) =>
        context.InvestmentAccounts
            .AsNoTracking()
            .OrderBy(value => value.IsArchived)
            .ThenBy(value => value.Name)
            .ThenBy(value => value.Id)
            .ToListAsync(cancellationToken);

    public async Task<PagedResult<InvestmentTransactionDto>> GetTransactionsAsync(
        Guid? accountId,
        Guid? instrumentId,
        string? type,
        DateOnly? from,
        DateOnly? to,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = context.InvestmentTransactions.AsNoTracking();
        if (accountId is not null) query = query.Where(value => value.AccountId == accountId);
        if (instrumentId is not null) query = query.Where(value => value.InstrumentId == instrumentId);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(value => value.Type == type);
        if (from is not null) query = query.Where(value => value.TradeDate >= from);
        if (to is not null) query = query.Where(value => value.TradeDate <= to);

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(value => value.TradeDate)
            .ThenByDescending(value => value.CreatedAt)
            .ThenByDescending(value => value.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<InvestmentTransactionDto>(
            rows.Select(InvestmentPortfolioService.ToDto).ToList(),
            total,
            page,
            pageSize);
    }

    public async Task<PagedResult<InvestmentCashFlowDto>> GetCashFlowsAsync(
        Guid? accountId,
        string? type,
        DateOnly? from,
        DateOnly? to,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = context.InvestmentCashFlows.AsNoTracking();
        if (accountId is not null) query = query.Where(value => value.AccountId == accountId);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(value => value.Type == type);
        if (from is not null) query = query.Where(value => value.Date >= from);
        if (to is not null) query = query.Where(value => value.Date <= to);

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(value => value.Date)
            .ThenByDescending(value => value.CreatedAt)
            .ThenByDescending(value => value.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<InvestmentCashFlowDto>(
            rows.Select(InvestmentPortfolioService.ToDto).ToList(),
            total,
            page,
            pageSize);
    }
}
