using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Investments;

/// <summary>One position's history, entirely in its instrument's own currency.</summary>
public sealed record CalculatedPosition(
    Guid AccountId,
    Guid InstrumentId,
    decimal Units,
    decimal CostBasisNative,
    decimal RealisedNative,
    decimal DividendsNative,
    decimal NetContributionsNative);

public sealed record InvestmentCalculation(IReadOnlyList<CalculatedPosition> Positions);

/// <summary>
/// Replays activity per position in the instrument's own currency and never converts it. The
/// portfolio is reported at constant currency: a figure in any other currency is the native figure
/// at today's rate, applied by whoever reads it. That is what lets the app's currency change
/// without a single cost, gain, or month-end being recomputed.
/// </summary>
public sealed class InvestmentAccountingService
{
    private sealed class MutablePosition
    {
        public decimal Units;
        public decimal Basis;
        public decimal Realised;
        public decimal Dividends;
        public decimal Contributions;
    }

    // opening: positions carried from a stored month-end snapshot. Replaying only the
    // transactions after that month-end from these positions gives the same result as
    // replaying the whole history, which is what lets a snapshot rebuild resume.
    public InvestmentCalculation Calculate(
        IEnumerable<InvestmentTransaction> transactions,
        IEnumerable<CalculatedPosition>? opening = null)
    {
        var positions = new Dictionary<(Guid AccountId, Guid InstrumentId), MutablePosition>();
        foreach (var carried in opening ?? [])
        {
            positions[(carried.AccountId, carried.InstrumentId)] = new MutablePosition
            {
                Units = carried.Units,
                Basis = carried.CostBasisNative,
                Realised = carried.RealisedNative,
                Dividends = carried.DividendsNative,
                Contributions = carried.NetContributionsNative,
            };
        }

        foreach (var transaction in OrderTransactions(transactions))
        {
            var key = (transaction.AccountId, transaction.InstrumentId);
            if (!positions.TryGetValue(key, out var position))
            {
                position = new MutablePosition();
                positions[key] = position;
            }

            var feesAndTaxes = transaction.Fees + transaction.Taxes;
            switch (transaction.Type)
            {
                case "Buy":
                {
                    RequirePositive(transaction.Units, "Units must be greater than zero.");
                    var basis = ResolveGross(transaction) + feesAndTaxes;
                    position.Units += transaction.Units;
                    position.Basis += basis;
                    position.Contributions += basis;
                    break;
                }
                case "Sell":
                {
                    RequireAvailableUnits(position, transaction.Units);
                    var allocatedBasis = AllocateBasis(position, transaction.Units);
                    var proceeds = ResolveGross(transaction) - feesAndTaxes;
                    position.Realised += proceeds - allocatedBasis;
                    position.Contributions -= proceeds;
                    break;
                }
                case "Dividend":
                {
                    if (position.Units <= 0)
                        throw new InvestmentValidationException("A dividend cannot be recorded before any units are held.");
                    var net = (transaction.CashAmount ?? 0) - feesAndTaxes;
                    if (net < 0) throw new InvestmentValidationException("Dividend cash cannot be less than fees and taxes.");
                    position.Dividends += net;
                    break;
                }
                case "FeeTax":
                {
                    var charge = (transaction.CashAmount ?? 0) + feesAndTaxes;
                    RequirePositive(charge, "A fee or tax amount is required.");
                    position.Realised -= charge;
                    break;
                }
                default:
                    throw new InvestmentValidationException($"Unsupported investment transaction type '{transaction.Type}'.");
            }
        }

        return new InvestmentCalculation(
            positions.Select(pair => new CalculatedPosition(
                    pair.Key.AccountId,
                    pair.Key.InstrumentId,
                    pair.Value.Units,
                    pair.Value.Basis,
                    pair.Value.Realised,
                    pair.Value.Dividends,
                    pair.Value.Contributions))
                .ToList());
    }

    private static IReadOnlyList<InvestmentTransaction> OrderTransactions(
        IEnumerable<InvestmentTransaction> transactions)
        => transactions
            .OrderBy(value => value.TradeDate)
            .ThenBy(value => value.CreatedAt)
            .ThenBy(value => value.Id)
            .ToList();

    private static decimal ResolveGross(InvestmentTransaction transaction)
    {
        var gross = transaction.CashAmount ??
                    (transaction.UnitPrice is > 0 ? transaction.UnitPrice.Value * transaction.Units : 0);
        RequirePositive(gross, "A positive unit price or gross amount is required.");
        return gross;
    }

    private static decimal AllocateBasis(MutablePosition position, decimal units)
    {
        var basis = position.Basis * (units / position.Units);
        position.Units -= units;
        position.Basis -= basis;
        if (Math.Abs(position.Units) < 0.0000000001m)
        {
            position.Units = 0;
            position.Basis = 0;
        }
        return basis;
    }

    private static void RequireAvailableUnits(MutablePosition position, decimal units)
    {
        RequirePositive(units, "Units must be greater than zero.");
        if (units > position.Units)
        {
            throw new InvestmentValidationException("This activity would sell or transfer more units than are held.");
        }
    }

    private static void RequirePositive(decimal value, string message)
    {
        if (value <= 0) throw new InvestmentValidationException(message);
    }
}

public sealed class InvestmentValidationException(string message) : Exception(message);
