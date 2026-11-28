using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Accounts;

/// <summary>
/// Where a card stands against its latest statement: the day it closed and what was charged to the
/// card after it. What is still due on that statement is what is owed today less those later
/// charges, so a payment, refund or rebate since the close counts against the statement while a new
/// purchase waits for the next one.
/// </summary>
public sealed record CardStatement(DateOnly StatementDate, decimal ChargedSince);

/// <summary>
/// Reads <see cref="CardStatement"/> for cards that record a statement day. Only the rows after the
/// latest close are read, so the cost is bounded by one statement period, never by account age.
/// </summary>
public sealed class CardStatementService
{
    private readonly AppDbContext _context;
    private readonly FinancialClock _clock;

    public CardStatementService(AppDbContext context, FinancialClock? clock = null)
    {
        _context = context;
        _clock = clock ?? FinancialClock.Utc;
    }

    /// <summary>
    /// The latest statement close before <paramref name="today"/>. A statement is issued at the end
    /// of its closing day, so on that day the previous one is still the latest. A day past the end
    /// of a short month closes on that month's last day.
    /// </summary>
    public static DateOnly LastStatementDate(int statementDay, DateOnly today)
    {
        var thisMonth = ClosingDate(today.Year, today.Month, statementDay);
        if (thisMonth < today) return thisMonth;
        var previous = today.AddMonths(-1);
        return ClosingDate(previous.Year, previous.Month, statementDay);
    }

    private static DateOnly ClosingDate(int year, int month, int statementDay) =>
        new(year, month, Math.Min(statementDay, DateTime.DaysInMonth(year, month)));

    public Task<IReadOnlyDictionary<string, CardStatement>> GetStatementsAsync(
        IReadOnlyCollection<LedgerAccount> accounts,
        CancellationToken cancellationToken = default) =>
        GetStatementsAsync(accounts, _clock.Today, cancellationToken);

    internal async Task<IReadOnlyDictionary<string, CardStatement>> GetStatementsAsync(
        IReadOnlyCollection<LedgerAccount> accounts,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var cards = accounts
            .Where(account => LedgerAccountKind.IsCreditCard(account.Kind) && account.StatementDay is not null)
            .Select(account => (Account: account, Closed: LastStatementDate(account.StatementDay!.Value, today)))
            .ToList();
        if (cards.Count == 0) return new Dictionary<string, CardStatement>(StringComparer.Ordinal);

        var charged = cards.ToDictionary(card => card.Account.Id, _ => 0m, StringComparer.Ordinal);
        var accountsById = accounts.ToDictionary(account => account.Id, StringComparer.Ordinal);
        var from = TransactionDate.ExclusiveEndOfDate(cards.Min(card => card.Closed));
        var cardIds = cards.Select(card => card.Account.Id).ToList();

        // Placement is explicit, so a row can only land on a card that it names.
        var rows = _context.Transactions.AsNoTracking()
            .Where(transaction => transaction.Date >= from
                && (cardIds.Contains(transaction.AccountId!) || cardIds.Contains(transaction.CounterAccountId!)))
            .Select(transaction => new Transaction
            {
                Date = transaction.Date,
                Category = transaction.Category,
                LedgerCategory = transaction.LedgerCategory,
                Amount = transaction.Amount,
                AccountId = transaction.AccountId,
                CounterAccountId = transaction.CounterAccountId,
            });
        await foreach (var transaction in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var date = TransactionDate.ToDateOnly(transaction.Date);
            foreach (var (account, closed) in cards)
            {
                if (date <= closed) continue;
                var amount = LedgerAccountAttribution.GetAccountAmount(transaction, account, accountsById);
                if (amount < 0m) charged[account.Id] -= amount;
            }
        }

        return cards.ToDictionary(
            card => card.Account.Id,
            card => new CardStatement(card.Closed, MoneyRounding.RoundMoney(charged[card.Account.Id])),
            StringComparer.Ordinal);
    }
}
