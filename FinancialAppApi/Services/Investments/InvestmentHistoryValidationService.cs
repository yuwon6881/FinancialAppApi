using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

/// <summary>
/// Validates only the accounts touched by the pending investment mutation. The previous
/// controller implementation loaded every investment and cash row twice for each mutation.
/// </summary>
public sealed class InvestmentHistoryValidationService(
    AppDbContext context,
    InvestmentAccountingService accountingService)
{
    public async Task<InvestmentHistoryValidationResult> ValidateTransactionMutationAsync(
        CancellationToken cancellationToken)
    {
        var accountIds = GetAffectedAccountIds();
        if (accountIds.Count == 0) return new InvestmentHistoryValidationResult(null, null);

        var transactions = await LoadTransactionsAsync(accountIds, cancellationToken);
        var flows = await LoadCashFlowsAsync(accountIds, cancellationToken);
        MergeTrackedEntities(transactions, context.ChangeTracker.Entries<InvestmentTransaction>());
        MergeTrackedEntities(flows, context.ChangeTracker.Entries<InvestmentCashFlow>());

        string? positionError;
        try
        {
            accountingService.Calculate(transactions, await GetAppCurrencyAsync(cancellationToken));
            positionError = null;
        }
        catch (InvestmentValidationException exception)
        {
            positionError = exception.Message;
        }

        return new InvestmentHistoryValidationResult(
            positionError,
            ValidateCashHistory(transactions, flows));
    }

    public async Task<string?> ValidatePositionHistoryAsync(CancellationToken cancellationToken)
    {
        var accountIds = GetAffectedAccountIds();
        if (accountIds.Count == 0) return null;

        var transactions = await LoadTransactionsAsync(accountIds, cancellationToken);
        MergeTrackedEntities(transactions, context.ChangeTracker.Entries<InvestmentTransaction>());

        try
        {
            accountingService.Calculate(transactions, await GetAppCurrencyAsync(cancellationToken));
            return null;
        }
        catch (InvestmentValidationException exception)
        {
            return exception.Message;
        }
    }

    public async Task<string?> ValidateCashHistoryAsync(CancellationToken cancellationToken)
    {
        var accountIds = GetAffectedAccountIds();
        if (accountIds.Count == 0) return null;

        var transactions = await LoadTransactionsAsync(accountIds, cancellationToken);
        var flows = await LoadCashFlowsAsync(accountIds, cancellationToken);

        MergeTrackedEntities(transactions, context.ChangeTracker.Entries<InvestmentTransaction>());
        MergeTrackedEntities(flows, context.ChangeTracker.Entries<InvestmentCashFlow>());

        return ValidateCashHistory(transactions, flows);
    }

    private static string? ValidateCashHistory(
        IEnumerable<InvestmentTransaction> transactions,
        IEnumerable<InvestmentCashFlow> flows)
    {
        var events = new List<InvestmentCashEvent>();
        foreach (var flow in flows)
        {
            events.Add(new InvestmentCashEvent(
                flow.Date,
                flow.CreatedAt,
                flow.Id,
                flow.AccountId,
                flow.Currency,
                flow.Amount));
            if (InvestmentPortfolioService.IsConversion(flow) &&
                flow.ToCurrency is not null &&
                flow.ToAmount is not null)
            {
                events.Add(new InvestmentCashEvent(
                    flow.Date,
                    flow.CreatedAt,
                    flow.Id,
                    flow.AccountId,
                    flow.ToCurrency,
                    flow.ToAmount.Value));
            }
        }

        foreach (var transaction in transactions)
        {
            var currency = transaction.Instrument.Currency;
            var feesAndTaxes = transaction.Fees + transaction.Taxes;
            var amount = transaction.Type switch
            {
                "Buy" => -((transaction.CashAmount ?? 0) + feesAndTaxes),
                "Sell" or "Dividend" => (transaction.CashAmount ?? 0) - feesAndTaxes,
                "FeeTax" => -((transaction.CashAmount ?? 0) + feesAndTaxes),
                _ => 0
            };
            if (amount != 0)
            {
                events.Add(new InvestmentCashEvent(
                    transaction.TradeDate,
                    transaction.CreatedAt,
                    transaction.Id,
                    transaction.AccountId,
                    currency,
                    amount));
            }
        }

        return ValidateCashEvents(events);
    }

    private Task<List<InvestmentTransaction>> LoadTransactionsAsync(
        IReadOnlySet<Guid> accountIds,
        CancellationToken cancellationToken) =>
        context.InvestmentTransactions
            .AsNoTracking()
            .Include(transaction => transaction.Instrument)
            .Where(transaction => accountIds.Contains(transaction.AccountId))
            .ToListAsync(cancellationToken);

    private Task<List<InvestmentCashFlow>> LoadCashFlowsAsync(
        IReadOnlySet<Guid> accountIds,
        CancellationToken cancellationToken) =>
        context.InvestmentCashFlows
            .AsNoTracking()
            .Where(flow => accountIds.Contains(flow.AccountId))
            .ToListAsync(cancellationToken);

    internal static string? ValidateCashEvents(IEnumerable<InvestmentCashEvent> events)
    {
        var balances = new Dictionary<(Guid AccountId, string Currency), decimal>();
        foreach (var cashEvent in events
                     .OrderBy(value => value.Date)
                     .ThenBy(value => value.CreatedAt)
                     .ThenBy(value => value.Id))
        {
            var key = (cashEvent.AccountId, cashEvent.Currency.ToUpperInvariant());
            var balance = balances.GetValueOrDefault(key) + cashEvent.Amount;
            if (balance < 0)
            {
                return $"Insufficient {key.Item2} cash in this account on {cashEvent.Date:yyyy-MM-dd}. Deposit or convert funds before recording this activity.";
            }
            balances[key] = balance;
        }
        return null;
    }

    private HashSet<Guid> GetAffectedAccountIds()
    {
        var accountIds = new HashSet<Guid>();
        AddAffectedAccountIds(context.ChangeTracker.Entries<InvestmentTransaction>(), accountIds);
        AddAffectedAccountIds(context.ChangeTracker.Entries<InvestmentCashFlow>(), accountIds);
        return accountIds;
    }

    private static void AddAffectedAccountIds<TEntity>(
        IEnumerable<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity>> entries,
        ISet<Guid> accountIds)
        where TEntity : class
    {
        foreach (var entry in entries.Where(entry => entry.State != EntityState.Unchanged))
        {
            if (entry.Metadata.FindProperty("AccountId") is null) continue;
            accountIds.Add(entry.CurrentValues.GetValue<Guid>("AccountId"));
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                accountIds.Add(entry.OriginalValues.GetValue<Guid>("AccountId"));
            }
        }
    }

    private static void MergeTrackedEntities<TEntity>(
        List<TEntity> databaseRows,
        IEnumerable<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity>> entries)
        where TEntity : class
    {
        var idProperty = typeof(TEntity).GetProperty("Id")
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} must expose an Id property.");
        var rowsById = databaseRows.ToDictionary(row => (Guid)idProperty.GetValue(row)!);

        foreach (var entry in entries.Where(entry => entry.State != EntityState.Unchanged))
        {
            var id = (Guid)idProperty.GetValue(entry.Entity)!;
            if (entry.State == EntityState.Deleted)
            {
                rowsById.Remove(id);
            }
            else
            {
                rowsById[id] = entry.Entity;
            }
        }

        databaseRows.Clear();
        databaseRows.AddRange(rowsById.Values);
    }

    private async Task<string> GetAppCurrencyAsync(CancellationToken cancellationToken) =>
        (await context.FinancialSettings
            .AsNoTracking()
            .Select(setting => setting.Currency)
            .FirstOrDefaultAsync(cancellationToken) ?? "USD").ToUpperInvariant();
}

internal sealed record InvestmentCashEvent(
    DateOnly Date,
    DateTime CreatedAt,
    Guid Id,
    Guid AccountId,
    string Currency,
    decimal Amount);

public sealed record InvestmentHistoryValidationResult(
    string? PositionError,
    string? CashError);
