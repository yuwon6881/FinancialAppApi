using System.Text.Json;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Investments;

public sealed record InvestmentCashCarry(Guid AccountId, string Currency, decimal Amount);

/// <summary>
/// Everything the next month needs from the one before it: no price or rate is carried.
/// Currencies lists every currency the portfolio has ever touched, so each one keeps a Total row
/// even after its cash and positions return to zero.
/// </summary>
public sealed record InvestmentCarryState(
    IReadOnlyList<CalculatedPosition> Positions,
    IReadOnlyList<InvestmentCashCarry> Cash,
    IReadOnlyList<string> Currencies)
{
    public static readonly InvestmentCarryState Empty = new([], [], []);

    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>Null when the stored value cannot be read: the caller must fall back to a full replay.</summary>
    public static InvestmentCarryState? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<InvestmentCarryState>(json);
            return state?.Positions is null || state.Cash is null || state.Currencies is null ? null : state;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One month of one segment in one currency, in that currency.</summary>
public sealed record InvestmentPeriodFigures(
    DateOnly PeriodEnd,
    string Segment,
    string Currency,
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
/// caller supplies prices, and a missing one leaves the figure null with a reason.
///
/// Every row is in one currency and nothing is converted (constant currency): a reader combines
/// currencies at today's rate, so a change of app currency never invalidates a stored month.
/// </summary>
public sealed class InvestmentPeriodCalculator(InvestmentAccountingService accounting)
{
    public IReadOnlyList<InvestmentPeriodResult> Walk(
        InvestmentCarryState opening,
        IReadOnlyDictionary<(string Segment, string Currency), decimal?> openingValues,
        DateOnly openingDate,
        IReadOnlyList<InvestmentTransaction> transactions,
        IReadOnlyList<InvestmentCashFlow> cashFlows,
        IReadOnlyDictionary<Guid, InvestmentInstrument> instruments,
        IReadOnlyList<DateOnly> monthEnds,
        Func<Guid, DateOnly, decimal?> price)
    {
        var results = new List<InvestmentPeriodResult>();
        var carry = opening;
        var previousValues = new Dictionary<(string, string), decimal?>(openingValues);
        var previousEnd = openingDate;
        foreach (var monthEnd in monthEnds)
        {
            var start = previousEnd;
            var monthTransactions = transactions.Where(value => value.TradeDate > start && value.TradeDate <= monthEnd).ToList();
            var monthFlows = cashFlows.Where(value => value.Date > start && value.Date <= monthEnd).ToList();
            var result = Month(carry, previousValues, monthEnd, monthTransactions, monthFlows, instruments, price);
            results.Add(result);
            carry = result.Carry;
            previousValues = result.Rows.ToDictionary(row => (row.Segment, row.Currency), row => row.ClosingValue);
            previousEnd = monthEnd;
        }
        return results;
    }

    private InvestmentPeriodResult Month(
        InvestmentCarryState opening,
        IReadOnlyDictionary<(string Segment, string Currency), decimal?> openingValues,
        DateOnly monthEnd,
        IReadOnlyList<InvestmentTransaction> transactions,
        IReadOnlyList<InvestmentCashFlow> flows,
        IReadOnlyDictionary<Guid, InvestmentInstrument> instruments,
        Func<Guid, DateOnly, decimal?> price)
    {
        var calculation = accounting.Calculate(transactions, opening.Positions);
        var currencies = new SortedSet<string>(opening.Currencies, StringComparer.Ordinal);
        var totalMissing = new SortedSet<string>(StringComparer.Ordinal);

        var cash = opening.Cash.ToDictionary(value => (value.AccountId, value.Currency), value => value.Amount);
        void AddCash(Guid accountId, string currency, decimal amount)
        {
            currency = currency.ToUpperInvariant();
            currencies.Add(currency);
            cash[(accountId, currency)] = cash.GetValueOrDefault((accountId, currency)) + amount;
        }
        var daysInMonth = monthEnd.Day;
        var contributions = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var weighted = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var flow in flows)
        {
            foreach (var (currency, amount) in InvestmentConstantCurrency.ContributionLegs(flow))
            {
                AddCash(flow.AccountId, currency, amount);
                contributions[currency] = contributions.GetValueOrDefault(currency) + amount;
                // Invested from the end of its day to the end of the month.
                weighted[currency] = weighted.GetValueOrDefault(currency) + amount * (daysInMonth - flow.Date.Day) / daysInMonth;
            }
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
        var segments = new Dictionary<(string Segment, string Currency), SegmentTotals>();
        foreach (var position in calculation.Positions)
        {
            if (!instruments.TryGetValue(position.InstrumentId, out var instrument))
            {
                totalMissing.Add("An investment in this history no longer exists.");
                continue;
            }
            var currency = instrument.Currency.ToUpperInvariant();
            currencies.Add(currency);
            var key = (InvestmentSnapshotSegments.For(instrument), currency);
            if (!segments.TryGetValue(key, out var totals)) segments[key] = totals = new SegmentTotals();
            before.TryGetValue((position.AccountId, position.InstrumentId), out var previous);
            totals.Contributions += position.NetContributionsNative - (previous?.NetContributionsNative ?? 0);
            totals.Dividends += position.DividendsNative - (previous?.DividendsNative ?? 0);
            totals.Realised += position.RealisedNative - (previous?.RealisedNative ?? 0);
            if (position.Units == 0) continue;
            totals.CostBasis += position.CostBasisNative;
            var close = price(instrument.Id, monthEnd);
            if (close is null) totals.Missing.Add($"No price for {instrument.Symbol} on {monthEnd:yyyy-MM-dd}.");
            totals.Value = close is null ? null : totals.Value + position.Units * close.Value;
        }

        var rows = new List<InvestmentPeriodFigures>();
        foreach (var currency in currencies)
        {
            var parts = segments.Where(pair => pair.Key.Currency == currency).Select(pair => pair.Value).ToList();
            var missing = new SortedSet<string>(totalMissing, StringComparer.Ordinal);
            foreach (var part in parts) missing.UnionWith(part.Missing);
            var market = parts.Aggregate((decimal?)0, (sum, value) => sum + value.Value);
            var cashValue = cash.Where(pair => pair.Key.Currency == currency).Sum(pair => pair.Value);
            rows.Add(new InvestmentPeriodFigures(
                monthEnd,
                InvestmentSnapshotSegments.Total,
                currency,
                openingValues.GetValueOrDefault((InvestmentSnapshotSegments.Total, currency), 0),
                market + cashValue,
                market,
                cashValue,
                contributions.GetValueOrDefault(currency),
                weighted.GetValueOrDefault(currency),
                parts.Sum(value => value.Dividends),
                parts.Sum(value => value.Realised),
                parts.Sum(value => value.CostBasis),
                missing.ToList()));
        }
        foreach (var ((segment, currency), totals) in segments
                     .OrderBy(pair => pair.Key.Segment, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.Currency, StringComparer.Ordinal))
        {
            var openingValue = openingValues.GetValueOrDefault((segment, currency), 0);
            // A market that has held nothing for good adds a row of zeros; the reader treats a
            // missing row as exactly that.
            if (openingValue == 0 && totals.Value == 0 && totals.Contributions == 0 && totals.Dividends == 0 &&
                totals.Realised == 0 && totals.CostBasis == 0 && totals.Missing.Count == 0)
                continue;
            // A segment's flows are its trades. Their exact timing inside the month is not kept,
            // so they count as invested for half of it -- the standard mid-period convention.
            rows.Add(new InvestmentPeriodFigures(
                monthEnd,
                segment,
                currency,
                openingValue,
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
                .ToList(),
            currencies.ToList());
        return new InvestmentPeriodResult(monthEnd, rows, carry);
    }

    private sealed class SegmentTotals
    {
        public decimal? Value = 0;
        public decimal Contributions;
        public decimal Dividends;
        public decimal Realised;
        public decimal CostBasis;
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
