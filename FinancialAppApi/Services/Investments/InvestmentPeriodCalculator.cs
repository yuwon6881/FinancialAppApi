using System.Text.Json;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Investments;

public sealed record InvestmentCashCarry(Guid AccountId, string Currency, decimal Amount);

/// <summary>Everything the next month needs from the one before it: no price or rate is carried.</summary>
public sealed record InvestmentCarryState(
    IReadOnlyList<CalculatedPosition> Positions,
    IReadOnlyList<InvestmentCashCarry> Cash)
{
    public static readonly InvestmentCarryState Empty = new([], []);

    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>Null when the stored value cannot be read: the caller must fall back to a full replay.</summary>
    public static InvestmentCarryState? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<InvestmentCarryState>(json);
            return state?.Positions is null || state.Cash is null ? null : state;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record InvestmentPeriodFigures(
    DateOnly PeriodEnd,
    string Segment,
    decimal? OpeningValue,
    decimal? ClosingValue,
    decimal? MarketValue,
    decimal? CashValue,
    decimal? NetContributions,
    decimal? WeightedContributions,
    decimal? Dividends,
    decimal? RealisedProfitLoss,
    decimal? CostBasis,
    IReadOnlyList<string> Missing)
{
    public bool IsComplete => Missing.Count == 0;
}

public sealed record InvestmentPeriodResult(
    DateOnly PeriodEnd,
    IReadOnlyList<InvestmentPeriodFigures> Rows,
    InvestmentCarryState Carry);

/// <summary>
/// Walks the portfolio forward one month-end at a time from a carried state. Each month replays
/// only its own activity, so a rebuild costs what changed, not the age of the account. Pure: the
/// caller supplies prices and rates, and a missing one leaves the figure null with a reason.
/// </summary>
public sealed class InvestmentPeriodCalculator(InvestmentAccountingService accounting)
{
    public IReadOnlyList<InvestmentPeriodResult> Walk(
        string appCurrency,
        InvestmentCarryState opening,
        IReadOnlyDictionary<string, decimal?> openingValues,
        DateOnly openingDate,
        IReadOnlyList<InvestmentTransaction> transactions,
        IReadOnlyList<InvestmentCashFlow> cashFlows,
        IReadOnlyDictionary<Guid, InvestmentInstrument> instruments,
        IReadOnlyList<DateOnly> monthEnds,
        Func<Guid, DateOnly, decimal?> price,
        Func<string, DateOnly, decimal?> fx)
    {
        var results = new List<InvestmentPeriodResult>();
        var carry = opening;
        var previousValues = new Dictionary<string, decimal?>(openingValues);
        var previousEnd = openingDate;
        foreach (var monthEnd in monthEnds)
        {
            var start = previousEnd;
            var monthTransactions = transactions.Where(value => value.TradeDate > start && value.TradeDate <= monthEnd).ToList();
            var monthFlows = cashFlows.Where(value => value.Date > start && value.Date <= monthEnd).ToList();
            var result = Month(appCurrency, carry, previousValues, monthEnd, monthTransactions, monthFlows, instruments, price, fx);
            results.Add(result);
            carry = result.Carry;
            previousValues = result.Rows.ToDictionary(row => row.Segment, row => row.ClosingValue);
            previousEnd = monthEnd;
        }
        return results;
    }

    private InvestmentPeriodResult Month(
        string appCurrency,
        InvestmentCarryState opening,
        IReadOnlyDictionary<string, decimal?> openingValues,
        DateOnly monthEnd,
        IReadOnlyList<InvestmentTransaction> transactions,
        IReadOnlyList<InvestmentCashFlow> flows,
        IReadOnlyDictionary<Guid, InvestmentInstrument> instruments,
        Func<Guid, DateOnly, decimal?> price,
        Func<string, DateOnly, decimal?> fx)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var calculation = accounting.Calculate(
            transactions,
            appCurrency,
            transaction => fx(transaction.Instrument.Currency, transaction.TradeDate),
            opening.Positions);
        foreach (var warning in calculation.Warnings) missing.Add(warning);

        var cash = opening.Cash.ToDictionary(value => (value.AccountId, value.Currency), value => value.Amount);
        void AddCash(Guid accountId, string currency, decimal amount)
            => cash[(accountId, currency)] = cash.GetValueOrDefault((accountId, currency)) + amount;
        var daysInMonth = monthEnd.Day;
        decimal? contributions = 0;
        decimal? weighted = 0;
        foreach (var flow in flows)
        {
            AddCash(flow.AccountId, flow.Currency, flow.Amount);
            if (flow.ToCurrency is not null && flow.ToAmount is not null)
                AddCash(flow.AccountId, flow.ToCurrency, flow.ToAmount.Value);
            // A conversion moves value between currencies without adding any.
            if (flow.Type.Equals("Conversion", StringComparison.OrdinalIgnoreCase)) continue;
            var rate = fx(flow.Currency, flow.Date);
            if (rate is null)
            {
                missing.Add($"No {flow.Currency}/{appCurrency} rate for a cash movement on {flow.Date:yyyy-MM-dd}.");
                contributions = null;
                weighted = null;
                continue;
            }
            var amount = flow.Amount * rate.Value;
            contributions += amount;
            // Invested from the end of its day to the end of the month.
            weighted += amount * (daysInMonth - flow.Date.Day) / daysInMonth;
        }
        foreach (var transaction in transactions)
        {
            var currency = transaction.Instrument.Currency;
            var charges = transaction.Fees + transaction.Taxes;
            var gross = transaction.CashAmount ?? 0;
            switch (transaction.Type)
            {
                case "Buy":
                case "FeeTax":
                    AddCash(transaction.AccountId, currency, -(gross + charges));
                    break;
                case "Sell":
                case "Dividend":
                    AddCash(transaction.AccountId, currency, gross - charges);
                    break;
            }
        }

        var before = opening.Positions.ToDictionary(value => (value.AccountId, value.InstrumentId));
        var segments = new Dictionary<string, SegmentTotals>(StringComparer.Ordinal);
        SegmentTotals SegmentFor(string key)
        {
            if (!segments.TryGetValue(key, out var totals)) segments[key] = totals = new SegmentTotals();
            return totals;
        }
        foreach (var position in calculation.Positions)
        {
            if (!instruments.TryGetValue(position.InstrumentId, out var instrument))
            {
                missing.Add("An investment in this history no longer exists.");
                continue;
            }
            var totals = SegmentFor(InvestmentSnapshotSegments.For(instrument));
            before.TryGetValue((position.AccountId, position.InstrumentId), out var previous);
            totals.Contributions = Delta(totals.Contributions, position.NetContributionsApp, previous?.NetContributionsApp ?? 0);
            totals.Dividends = Delta(totals.Dividends, position.DividendsApp, previous?.DividendsApp ?? 0);
            totals.Realised = Delta(totals.Realised, position.RealisedApp, previous?.RealisedApp ?? 0);
            totals.CostBasis = position.CostBasisApp is null ? null : totals.CostBasis + position.CostBasisApp;
            if (position.Units == 0) continue;
            var close = price(instrument.Id, monthEnd);
            var rate = fx(instrument.Currency, monthEnd);
            if (close is null) totals.Missing.Add($"No price for {instrument.Symbol} on {monthEnd:yyyy-MM-dd}.");
            if (rate is null) totals.Missing.Add($"No {instrument.Currency}/{appCurrency} rate on {monthEnd:yyyy-MM-dd}.");
            totals.Value = close is null || rate is null ? null : totals.Value + position.Units * close.Value * rate.Value;
        }

        decimal? cashValue = 0;
        foreach (var ((_, currency), amount) in cash)
        {
            if (amount == 0) continue;
            var rate = fx(currency, monthEnd);
            if (rate is null)
            {
                missing.Add($"No {currency}/{appCurrency} rate on {monthEnd:yyyy-MM-dd}.");
                cashValue = null;
            }
            else if (cashValue is not null)
            {
                cashValue += amount * rate.Value;
            }
        }

        foreach (var totals in segments.Values)
        {
            // A trade with no rate for its own date leaves its flows unknown (the reason is
            // already among the accounting warnings on the Total row).
            if (totals.Contributions is null || totals.Dividends is null || totals.Realised is null || totals.CostBasis is null)
                totals.Missing.Add("A trade in this market has no exchange rate for its date.");
            missing.UnionWith(totals.Missing);
        }
        var missingList = missing.ToList();
        var rows = new List<InvestmentPeriodFigures>();
        decimal? marketValue = segments.Values.Aggregate((decimal?)0, (sum, value) => sum + value.Value);
        rows.Add(new InvestmentPeriodFigures(
            monthEnd,
            InvestmentSnapshotSegments.Total,
            openingValues.GetValueOrDefault(InvestmentSnapshotSegments.Total, 0),
            marketValue + cashValue,
            marketValue,
            cashValue,
            contributions,
            weighted,
            segments.Values.Aggregate((decimal?)0, (sum, value) => sum + value.Dividends),
            segments.Values.Aggregate((decimal?)0, (sum, value) => sum + value.Realised),
            segments.Values.Aggregate((decimal?)0, (sum, value) => sum + value.CostBasis),
            missingList));
        foreach (var (segment, totals) in segments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            // A segment's flows are its trades. Their exact timing inside the month is not kept,
            // so they count as invested for half of it -- the standard mid-period convention.
            rows.Add(new InvestmentPeriodFigures(
                monthEnd,
                segment,
                openingValues.GetValueOrDefault(segment, 0),
                totals.Value,
                totals.Value,
                null,
                totals.Contributions,
                totals.Contributions / 2,
                totals.Dividends,
                totals.Realised,
                totals.CostBasis,
                totals.Missing.ToList()));
        }

        var carry = new InvestmentCarryState(
            calculation.Positions.ToList(),
            cash.Where(pair => pair.Value != 0)
                .Select(pair => new InvestmentCashCarry(pair.Key.AccountId, pair.Key.Currency, pair.Value))
                .OrderBy(value => value.AccountId).ThenBy(value => value.Currency, StringComparer.Ordinal)
                .ToList());
        return new InvestmentPeriodResult(monthEnd, rows, carry);
    }

    private static decimal? Delta(decimal? sum, decimal? after, decimal? before)
        => sum is null || after is null || before is null ? null : sum + after - before;

    private sealed class SegmentTotals
    {
        public decimal? Value = 0;
        public decimal? Contributions = 0;
        public decimal? Dividends = 0;
        public decimal? Realised = 0;
        public decimal? CostBasis = 0;
        public readonly SortedSet<string> Missing = new(StringComparer.Ordinal);
    }

    /// <summary>The last calendar day of every month from the one holding <paramref name="first"/> through <paramref name="last"/>.</summary>
    public static IReadOnlyList<DateOnly> MonthEndsBetween(DateOnly first, DateOnly last)
    {
        var ends = new List<DateOnly>();
        for (var end = MonthEnd(first); end <= last; end = MonthEnd(end.AddDays(1)))
            ends.Add(end);
        return ends;
    }

    public static DateOnly MonthEnd(DateOnly date)
        => new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
}
