using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Investments;

public sealed record CalculatedPosition(
    Guid AccountId,
    Guid InstrumentId,
    decimal Units,
    decimal CostBasisNative,
    decimal? CostBasisApp,
    decimal RealisedNative,
    decimal? RealisedApp,
    decimal DividendsNative,
    decimal? DividendsApp,
    decimal NetContributionsNative,
    decimal? NetContributionsApp);

public sealed record InvestmentCalculation(
    IReadOnlyList<CalculatedPosition> Positions,
    IReadOnlyList<string> Warnings);

public sealed class InvestmentAccountingService
{
    private sealed class MutablePosition
    {
        public decimal Units;
        public decimal Basis;
        public decimal? BasisApp = 0;
        public decimal Realised;
        public decimal? RealisedApp = 0;
        public decimal Dividends;
        public decimal? DividendsApp = 0;
        public decimal Contributions;
        public decimal? ContributionsApp = 0;
    }

    private sealed record TransferBasis(decimal Units, decimal Native, decimal? App);

    // historicalFx: optional fallback supplying the reporting FX rate for a
    // transaction whose instrument currency differs from the app currency. Lets
    // foreign-currency dividends, fees, and trades be valued at the market rate on
    // the trade date (e.g. a stored provider daily close) instead of forcing the
    // user to type a rate they never executed.
    public InvestmentCalculation Calculate(
        IEnumerable<InvestmentTransaction> transactions,
        string appCurrency,
        Func<InvestmentTransaction, decimal?>? historicalFx = null)
    {
        var positions = new Dictionary<(Guid AccountId, Guid InstrumentId), MutablePosition>();
        var transferBasis = new Dictionary<Guid, TransferBasis>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);

        foreach (var transaction in OrderTransactions(transactions))
        {
            var key = (transaction.AccountId, transaction.InstrumentId);
            if (!positions.TryGetValue(key, out var position))
            {
                position = new MutablePosition();
                positions[key] = position;
            }

            var fx = ResolveTradeFx(transaction, appCurrency, historicalFx);
            if (fx is null)
            {
                warnings.Add($"Historical FX is missing for {transaction.Instrument.Symbol} on {transaction.TradeDate:yyyy-MM-dd}.");
            }
            var feesAndTaxes = transaction.Fees + transaction.Taxes;
            switch (transaction.Type)
            {
                case "OpeningPosition":
                case "Buy":
                {
                    RequirePositive(transaction.Units, "Units must be greater than zero.");
                    var gross = ResolveGross(transaction);
                    var basis = gross + feesAndTaxes;
                    position.Units += transaction.Units;
                    position.Basis += basis;
                    position.Contributions += basis;
                    AddConverted(ref position.BasisApp, basis, fx);
                    AddConverted(ref position.ContributionsApp, basis, fx);
                    break;
                }
                case "Sell":
                {
                    RequireAvailableUnits(position, transaction.Units);
                    var allocatedBasis = AllocateBasis(position, transaction.Units);
                    var proceeds = ResolveGross(transaction) - feesAndTaxes;
                    position.Realised += proceeds - allocatedBasis.Native;
                    if (fx is null || allocatedBasis.App is null)
                    {
                        position.RealisedApp = null;
                    }
                    else if (position.RealisedApp is not null)
                    {
                        position.RealisedApp += proceeds * fx.Value - allocatedBasis.App.Value;
                    }
                    position.Contributions -= proceeds;
                    AddConverted(ref position.ContributionsApp, -proceeds, fx);
                    break;
                }
                case "Dividend":
                {
                    var net = (transaction.CashAmount ?? 0) - feesAndTaxes;
                    if (net < 0) throw new InvestmentValidationException("Dividend cash cannot be less than fees and taxes.");
                    position.Dividends += net;
                    AddConverted(ref position.DividendsApp, net, fx);
                    break;
                }
                case "FeeTax":
                {
                    var charge = (transaction.CashAmount ?? 0) + feesAndTaxes;
                    RequirePositive(charge, "A fee or tax amount is required.");
                    position.Realised -= charge;
                    AddConverted(ref position.RealisedApp, -charge, fx);
                    break;
                }
                case "Split":
                {
                    RequirePositive(transaction.Units, "Split ratio must be greater than zero.");
                    position.Units *= transaction.Units;
                    break;
                }
                case "TransferOut":
                {
                    RequireAvailableUnits(position, transaction.Units);
                    var allocated = AllocateBasis(position, transaction.Units);
                    transferBasis[transaction.Id] = new TransferBasis(transaction.Units, allocated.Native, allocated.App);
                    break;
                }
                case "TransferIn":
                {
                    RequirePositive(transaction.Units, "Units must be greater than zero.");
                    TransferBasis transferred;
                    if (transaction.LinkedTransferId is Guid linkedId &&
                        transferBasis.Remove(linkedId, out var linked))
                    {
                        if (linked.Units != transaction.Units)
                        {
                            throw new InvestmentValidationException(
                                "Linked transfer-in units must match the transfer-out units.");
                        }
                        transferred = linked;
                    }
                    else if (transaction.LinkedTransferId is not null)
                    {
                        throw new InvestmentValidationException(
                            "A linked transfer-in must reference one unused transfer-out.");
                    }
                    else
                    {
                        var basis = transaction.CashAmount ??
                            throw new InvestmentValidationException("External transfer-in requires transferred cost basis.");
                        RequirePositive(basis, "Transferred cost basis must be greater than zero.");
                        transferred = new TransferBasis(
                            transaction.Units,
                            basis,
                            fx is null ? null : basis * fx.Value);
                    }
                    position.Units += transaction.Units;
                    position.Basis += transferred.Native;
                    if (position.BasisApp is null || transferred.App is null) position.BasisApp = null;
                    else position.BasisApp += transferred.App.Value;
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
                    pair.Value.BasisApp,
                    pair.Value.Realised,
                    pair.Value.RealisedApp,
                    pair.Value.Dividends,
                    pair.Value.DividendsApp,
                    pair.Value.Contributions,
                    pair.Value.ContributionsApp))
                .ToList(),
            warnings.Order().ToList());
    }

    private static IReadOnlyList<InvestmentTransaction> OrderTransactions(
        IEnumerable<InvestmentTransaction> transactions)
    {
        var ordered = transactions
            .OrderBy(value => value.TradeDate)
            .ThenBy(value => value.CreatedAt)
            .ThenBy(value => value.Id)
            .ToList();

        // PostgreSQL timestamp precision can make paired legs tie, leaving GUID ordering to put
        // the dependent transfer-in first. Move only that leg behind its own transfer-out;
        // unrelated same-day activity retains its stable chronological order.
        for (var index = 0; index < ordered.Count; index++)
        {
            var incoming = ordered[index];
            if (incoming.Type != "TransferIn" || incoming.LinkedTransferId is not Guid sourceId)
                continue;
            var sourceIndex = ordered.FindIndex(index + 1, value => value.Id == sourceId);
            if (sourceIndex < 0) continue;
            ordered.RemoveAt(index);
            sourceIndex--;
            ordered.Insert(sourceIndex + 1, incoming);
        }

        return ordered;
    }

    private static decimal? ResolveTradeFx(
        InvestmentTransaction transaction,
        string appCurrency,
        Func<InvestmentTransaction, decimal?>? historicalFx)
    {
        if (transaction.Instrument.Currency.Equals(appCurrency, StringComparison.OrdinalIgnoreCase))
            return 1m;
        // Value the amount at the market rate for its trade date.
        var fallback = historicalFx?.Invoke(transaction);
        return fallback is > 0 ? fallback : null;
    }

    private static decimal ResolveGross(InvestmentTransaction transaction)
    {
        var gross = transaction.CashAmount ??
                    (transaction.UnitPrice is > 0 ? transaction.UnitPrice.Value * transaction.Units : 0);
        RequirePositive(gross, "A positive unit price or gross amount is required.");
        return gross;
    }

    private static TransferBasis AllocateBasis(MutablePosition position, decimal units)
    {
        var ratio = units / position.Units;
        var basis = position.Basis * ratio;
        var app = position.BasisApp * ratio;
        position.Units -= units;
        position.Basis -= basis;
        if (position.BasisApp is not null && app is not null) position.BasisApp -= app.Value;
        if (Math.Abs(position.Units) < 0.0000000001m)
        {
            position.Units = 0;
            position.Basis = 0;
            if (position.BasisApp is not null) position.BasisApp = 0;
        }
        return new TransferBasis(units, basis, app);
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

    private static void AddConverted(ref decimal? target, decimal native, decimal? fx)
    {
        if (target is null || fx is null)
        {
            target = null;
            return;
        }
        target += native * fx.Value;
    }
}

public sealed class InvestmentValidationException(string message) : Exception(message);
