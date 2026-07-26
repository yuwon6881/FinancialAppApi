using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

public sealed record InvestmentSummaryDto(
    decimal GrowthLedgerBalance,
    decimal GrowthContributions,
    decimal? NetDeposits,
    decimal? MarketValue,
    decimal? CostBasis,
    decimal? UnrealisedProfitLoss,
    decimal? UnrealisedPercent,
    decimal? RealisedProfitLoss,
    decimal? NetDividends,
    decimal? DailyChange,
    decimal? CashValue,
    decimal? TotalValue);

public sealed record InvestmentCashBalanceDto(
    Guid AccountId,
    string AccountName,
    string Currency,
    decimal Amount,
    decimal? AmountApp);

public sealed record InvestmentContributionDto(DateOnly Date, decimal AmountApp);

public sealed record InvestmentCashFlowDto(
    Guid Id,
    Guid AccountId,
    string Currency,
    string Type,
    decimal Amount,
    DateOnly Date,
    string? ToCurrency = null,
    decimal? ToAmount = null,
    DateTime? CreatedAt = null);

public sealed record InvestmentHoldingDto(
    Guid AccountId,
    string AccountName,
    Guid InstrumentId,
    string Symbol,
    string Name,
    string Type,
    string Currency,
    decimal Units,
    decimal AverageCostNative,
    decimal? LatestPriceNative,
    decimal? ValueNative,
    decimal? ValueApp,
    decimal? DailyChangeApp,
    decimal? UnrealisedProfitLossApp,
    decimal? UnrealisedPercent,
    DateOnly? PriceDate,
    DateTime? PriceFetchedAt,
    bool UsesManualPrice,
    bool FxIncomplete,
    decimal? FxRate,
    DateOnly? FxDate,
    DateTime? FxFetchedAt,
    string? FxSource,
    string? PriceSource,
    DateOnly? ValuationAsOf);

public sealed record InvestmentChartPointDto(
    DateOnly Date,
    decimal? TotalValue,
    decimal? NetDeposits);

public sealed record InvestmentAccountSetupDto(
    Guid Id,
    string Name,
    string BaseCurrency,
    bool IsArchived,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool CanDelete,
    bool CanArchive,
    string? ArchiveUnavailableReason);

public sealed record InvestmentInstrumentSetupDto(
    Guid Id,
    string Symbol,
    string Name,
    string Type,
    string? Exchange,
    string? Mic,
    string? Country,
    string Currency,
    string? ProviderSymbol,
    string? ProviderMic,
    bool IsCustom,
    bool IsArchived,
    string? AllocationSleeve,
    int AllocationOrder,
    bool CanDelete,
    bool CanArchive,
    string? ArchiveUnavailableReason);

public sealed record InvestmentPortfolioDto(
    string AppCurrency,
    decimal? UsdRate,
    InvestmentSummaryDto Summary,
    IReadOnlyList<InvestmentAccountSetupDto> Accounts,
    IReadOnlyList<InvestmentInstrumentSetupDto> Instruments,
    IReadOnlyList<InvestmentHoldingDto> Holdings,
    IReadOnlyList<ManualPriceDto> ManualPrices,
    IReadOnlyList<InvestmentChartPointDto> Chart,
    IReadOnlyList<InvestmentCashBalanceDto> CashBalances,
    int ActivityCount,
    int CashFlowCount,
    IReadOnlyList<string> Insights,
    IReadOnlyList<string> Warnings,
    DateTime? PricesUpdatedAt,
    bool MarketDataConfigured,
    InvestmentAllocationOverviewDto Allocation);

public sealed record InvestmentTransactionDto(
    Guid Id,
    Guid AccountId,
    Guid InstrumentId,
    string Type,
    DateOnly TradeDate,
    decimal Units,
    decimal? UnitPrice,
    decimal? CashAmount,
    decimal Fees,
    decimal Taxes,
    Guid? LinkedTransferId,
    DateTime CreatedAt,
    bool IsPairedTransfer = false);

public sealed record ManualPriceDto(
    Guid Id,
    Guid InstrumentId,
    DateOnly MarketDate,
    decimal Price);

public sealed class InvestmentPortfolioService(
    AppDbContext context,
    InvestmentAccountingService accounting,
    IMarketDataProvider provider,
    InvestmentAllocationService? allocationService = null)
{
    public async Task<InvestmentPortfolioDto> GetPortfolioAsync(
        string range,
        CancellationToken cancellationToken)
    {
        var appCurrency = (await context.FinancialSettings.AsNoTracking()
                .Select(value => value.Currency)
                .FirstOrDefaultAsync(cancellationToken) ?? "USD")
            .ToUpperInvariant();
        var accounts = await context.InvestmentAccounts.AsNoTracking()
            .OrderBy(value => value.IsArchived).ThenBy(value => value.Name)
            .ToListAsync(cancellationToken);
        var instruments = await context.InvestmentInstruments.AsNoTracking()
            .OrderBy(value => value.Symbol)
            .ToListAsync(cancellationToken);
        var transactions = await context.InvestmentTransactions.AsNoTracking()
            .Include(value => value.Instrument)
            .Include(value => value.Account)
            .OrderByDescending(value => value.TradeDate)
            .ThenByDescending(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        var overrides = await context.ManualPriceOverrides.AsNoTracking()
            .OrderByDescending(value => value.MarketDate)
            .ToListAsync(cancellationToken);
        var cashFlows = await context.InvestmentCashFlows.AsNoTracking()
            .OrderByDescending(value => value.Date)
            .ThenByDescending(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        var ledgerTransactions = await context.Transactions.AsNoTracking()
            .OrderBy(value => value.Date)
            .ToListAsync(cancellationToken);

        var symbols = instruments
            .Where(value => !value.IsCustom && !string.IsNullOrWhiteSpace(value.ProviderSymbol))
            .Select(value => value.ProviderSymbol!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var priceBars = symbols.Count == 0
            ? []
            : await context.MarketPriceBars.AsNoTracking()
                .Where(value => value.Provider == "twelvedata" && symbols.Contains(value.Symbol))
                .ToListAsync(cancellationToken);
        var currencies = instruments.Select(value => value.Currency)
            .Concat(cashFlows.Select(value => value.Currency))
            .Concat(cashFlows.Where(value => value.ToCurrency is not null).Select(value => value.ToCurrency!))
            .Distinct()
            .ToList();
        var fxBars = await context.FxRateBars.AsNoTracking()
            .Where(value => value.Provider == "twelvedata" &&
                            (currencies.Contains(value.BaseCurrency) ||
                             currencies.Contains(value.QuoteCurrency) ||
                             value.BaseCurrency == "USD" ||
                             value.QuoteCurrency == "USD"))
            .ToListAsync(cancellationToken);

        // Value foreign-currency dividends, fees, and trades at the market rate on
        // their trade date (stored provider daily close), mirroring how current
        // holdings are valued.
        decimal? HistoricalTradeFx(InvestmentTransaction transaction) =>
            ResolveFx(transaction.Instrument.Currency, appCurrency, transaction.TradeDate, fxBars)?.Rate;
        var calculation = accounting.Calculate(transactions, appCurrency, HistoricalTradeFx);
        var accountById = accounts.ToDictionary(value => value.Id);
        var instrumentById = instruments.ToDictionary(value => value.Id);
        var holdings = new List<InvestmentHoldingDto>();
        var warnings = calculation.Warnings.ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var position in calculation.Positions.Where(value => value.Units != 0))
        {
            if (!accountById.TryGetValue(position.AccountId, out var account) ||
                !instrumentById.TryGetValue(position.InstrumentId, out var instrument))
            {
                continue;
            }

            var prices = ResolvePrices(instrument, priceBars, overrides);
            var latest = prices.LastOrDefault();
            var previous = prices.Count > 1 ? prices[^2] : null;
            var fx = ResolveFx(instrument.Currency, appCurrency, today, fxBars);
            decimal? valueNative = latest is null ? null : latest.Price * position.Units;
            decimal? valueApp = valueNative is not null && fx is not null ? valueNative * fx.Rate : null;
            decimal? unrealised = valueApp is not null && position.CostBasisApp is not null
                ? valueApp - position.CostBasisApp
                : null;
            decimal? daily = latest is not null && previous is not null && fx is not null
                ? (latest.Price - previous.Price) * position.Units * fx.Rate
                : null;
            var incomplete = instrument.Currency != appCurrency && fx is null;
            if (incomplete)
            {
                warnings.Add($"Current FX is missing for {instrument.Currency}/{appCurrency}; converted totals are incomplete.");
            }

            holdings.Add(new InvestmentHoldingDto(
                account.Id,
                account.Name,
                instrument.Id,
                instrument.Symbol,
                instrument.Name,
                instrument.Type,
                instrument.Currency,
                position.Units,
                position.Units == 0 ? 0 : position.CostBasisNative / position.Units,
                latest?.Price,
                valueNative,
                valueApp,
                daily,
                unrealised,
                unrealised is not null && position.CostBasisApp is > 0
                    ? unrealised / position.CostBasisApp.Value * 100m
                    : null,
                latest?.Date,
                latest?.FetchedAt,
                latest?.Manual == true,
                incomplete,
                fx?.Rate,
                fx?.Date,
                fx?.FetchedAt,
                fx?.Source,
                latest is null ? null : latest.Manual ? "Manual close" : "Twelve Data daily close",
                latest is null || fx is null ? null : latest.Date < fx.Date ? latest.Date : fx.Date));
        }

        var convertedComplete = holdings.All(value => value.ValueApp is not null) &&
                                calculation.Positions.All(value =>
                                    value.CostBasisApp is not null &&
                                    value.RealisedApp is not null &&
                                    value.DividendsApp is not null);
        decimal? marketValue = convertedComplete ? holdings.Sum(value => value.ValueApp ?? 0) : null;
        decimal? costBasis = convertedComplete
            ? calculation.Positions.Where(value => value.Units != 0).Sum(value => value.CostBasisApp ?? 0)
            : null;
        decimal? realised = convertedComplete ? calculation.Positions.Sum(value => value.RealisedApp ?? 0) : null;
        decimal? dividends = convertedComplete ? calculation.Positions.Sum(value => value.DividendsApp ?? 0) : null;
        decimal? unrealisedTotal = marketValue is not null && costBasis is not null ? marketValue - costBasis : null;
        var growthAmounts = ledgerTransactions
            .Select(value => new
            {
                Date = DateOnly.FromDateTime(value.Date),
                Amount = CategoryAttributionService.GetCategoryAmount(value, "Growth")
            })
            .ToList();
        var growthLedger = growthAmounts.Sum(value => value.Amount);
        var growthContributions = growthAmounts.Where(value => value.Amount > 0)
            .Sum(value => value.Amount);

        // Uninvested cash per account+currency: explicit deposits/withdrawals plus
        // the implicit cash effect of trades and income. Opening positions declare
        // existing holdings and do not move cash.
        var cashByKey = new Dictionary<(Guid AccountId, string Currency), decimal>();
        void AddCash(Guid accountId, string currency, decimal amount)
        {
            var key = (accountId, currency.ToUpperInvariant());
            cashByKey[key] = cashByKey.GetValueOrDefault(key) + amount;
        }
        foreach (var flow in cashFlows)
        {
            AddCash(flow.AccountId, flow.Currency, flow.Amount);
            // A conversion also credits the bought currency; its Amount leg is
            // already stored negative, so the pair nets to zero in value terms.
            if (flow.ToCurrency is not null && flow.ToAmount is not null)
                AddCash(flow.AccountId, flow.ToCurrency, flow.ToAmount.Value);
        }
        foreach (var transaction in transactions)
        {
            var currency = transaction.Instrument.Currency;
            var feesAndTaxes = transaction.Fees + transaction.Taxes;
            switch (transaction.Type)
            {
                case "Buy":
                    AddCash(transaction.AccountId, currency, -((transaction.CashAmount ?? 0) + feesAndTaxes));
                    break;
                case "Sell":
                    AddCash(transaction.AccountId, currency, (transaction.CashAmount ?? 0) - feesAndTaxes);
                    break;
                case "Dividend":
                    AddCash(transaction.AccountId, currency, (transaction.CashAmount ?? 0) - feesAndTaxes);
                    break;
                case "FeeTax":
                    AddCash(transaction.AccountId, currency, -((transaction.CashAmount ?? 0) + feesAndTaxes));
                    break;
            }
        }

        decimal? CurrencyFx(string currency) =>
            ResolveFx(currency, appCurrency, today, fxBars)?.Rate;

        var cashBalances = cashByKey
            .Where(pair => pair.Value != 0)
            .Select(pair =>
            {
                var fx = CurrencyFx(pair.Key.Currency);
                if (fx is null)
                {
                    warnings.Add($"Current FX is missing for {pair.Key.Currency}/{appCurrency}; cash totals are incomplete.");
                }
                return new InvestmentCashBalanceDto(
                    pair.Key.AccountId,
                    accountById.TryGetValue(pair.Key.AccountId, out var account) ? account.Name : "Account",
                    pair.Key.Currency,
                    pair.Value,
                    fx is null ? null : pair.Value * fx.Value);
            })
            .OrderByDescending(value => value.AmountApp ?? decimal.MinValue)
            .ToList();
        var cashComplete = cashBalances.All(value => value.AmountApp is not null);
        decimal? cashValue = cashComplete ? cashBalances.Sum(value => value.AmountApp ?? 0) : null;
        decimal? totalValue = marketValue is not null && cashValue is not null ? marketValue + cashValue : null;
        var contributionHistory = growthAmounts
            .Where(value => value.Amount > 0)
            .Select(value => new InvestmentContributionDto(value.Date, value.Amount))
            .ToList();
        decimal? netDeposits = 0;
        foreach (var flow in cashFlows)
        {
            // Conversions move value between currencies without adding any, so
            // counting them here would book a phantom contribution.
            if (IsConversion(flow)) continue;
            var fx = ResolveFx(flow.Currency, appCurrency, flow.Date, fxBars)?.Rate;
            if (fx is null)
            {
                netDeposits = null;
                break;
            }
            netDeposits += flow.Amount * fx.Value;
        }

        var chart = BuildChart(range, transactions, cashFlows, instruments, priceBars, fxBars, overrides, appCurrency);
        var latestFetchedAt = holdings.Where(value => value.PriceFetchedAt is not null)
            .Select(value => value.PriceFetchedAt)
            .Max();
        var summary = new InvestmentSummaryDto(
            growthLedger,
            growthContributions,
            netDeposits is null ? null : RoundMoney(netDeposits.Value),
            marketValue,
            costBasis,
            unrealisedTotal,
            unrealisedTotal is not null && costBasis is > 0 ? unrealisedTotal / costBasis * 100m : null,
            realised,
            dividends,
            convertedComplete ? holdings.Sum(value => value.DailyChangeApp ?? 0) : null,
            cashValue,
            totalValue);

        var openKeys = calculation.Positions.Where(value => value.Units != 0)
            .Select(value => (value.AccountId, value.InstrumentId)).ToHashSet();
        var nonZeroCashAccounts = cashBalances.Where(value => value.Amount != 0)
            .Select(value => value.AccountId).ToHashSet();
        var accountDtos = accounts.Select(value =>
        {
            var hasHistory = transactions.Any(tx => tx.AccountId == value.Id) ||
                             cashFlows.Any(flow => flow.AccountId == value.Id);
            var canArchive = !openKeys.Any(key => key.AccountId == value.Id) &&
                             !nonZeroCashAccounts.Contains(value.Id);
            return new InvestmentAccountSetupDto(
                value.Id, value.Name, value.BaseCurrency, value.IsArchived, value.CreatedAt, value.UpdatedAt,
                !hasHistory, canArchive,
                canArchive ? null : "Close all positions and bring every cash balance to zero before archiving.");
        }).ToList();
        var instrumentDtos = instruments.Select(value =>
        {
            var hasHistory = transactions.Any(tx => tx.InstrumentId == value.Id) ||
                             overrides.Any(price => price.InstrumentId == value.Id);
            var canArchive = !openKeys.Any(key => key.InstrumentId == value.Id);
            return new InvestmentInstrumentSetupDto(
                value.Id, value.Symbol, value.Name, value.Type, value.Exchange, value.Mic, value.Country,
                value.Currency, value.ProviderSymbol, value.ProviderMic, value.IsCustom, value.IsArchived,
                value.AllocationSleeve, value.AllocationOrder,
                !hasHistory, canArchive, canArchive ? null : "Close all units before archiving this investment.");
        }).ToList();

        var allocation = await (allocationService ?? new InvestmentAllocationService(context)).BuildAsync(
            appCurrency, holdings, instrumentDtos, cashBalances, contributionHistory,
            provider.IsConfigured, cancellationToken);
        var usdRate = appCurrency.Equals("USD", StringComparison.OrdinalIgnoreCase)
            ? 1m
            : ResolveFx("USD", appCurrency, today, fxBars)?.Rate;

        return new InvestmentPortfolioDto(
            appCurrency,
            usdRate,
            summary,
            accountDtos,
            instrumentDtos,
            holdings.OrderByDescending(value => value.ValueApp ?? decimal.MinValue).ToList(),
            overrides.Select(ToDto).ToList(),
            chart,
            cashBalances,
            transactions.Count,
            cashFlows.Count,
            BuildInsights(holdings, warnings),
            warnings.Distinct().ToList(),
            latestFetchedAt,
            provider.IsConfigured,
            allocation);
    }

    private IReadOnlyList<InvestmentChartPointDto> BuildChart(
        string range,
        IReadOnlyList<InvestmentTransaction> transactions,
        IReadOnlyList<InvestmentCashFlow> cashFlows,
        IReadOnlyList<InvestmentInstrument> instruments,
        IReadOnlyList<MarketPriceBar> priceBars,
        IReadOnlyList<FxRateBar> fxBars,
        IReadOnlyList<ManualPriceOverride> overrides,
        string appCurrency)
    {
        if (transactions.Count == 0 && cashFlows.Count == 0) return [];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = range.ToLowerInvariant() switch
        {
            "1m" => today.AddMonths(-1),
            "3m" => today.AddMonths(-3),
            "6m" => today.AddMonths(-6),
            "1y" => today.AddYears(-1),
            _ => transactions.Select(value => value.TradeDate)
                .Concat(cashFlows.Select(value => value.Date))
                .Min()
        };
        var dates = transactions.Select(value => value.TradeDate)
            .Concat(cashFlows.Select(value => value.Date))
            .Concat(priceBars.Select(value => value.MarketDate))
            .Concat(overrides.Select(value => value.MarketDate))
            .Append(today)
            .Where(value => value >= from && value <= today)
            .Distinct()
            .Order()
            .ToList();
        if (dates.Count > 180)
        {
            var interval = (int)Math.Ceiling(dates.Count / 180m);
            dates = dates.Where((_, index) => index % interval == 0).Append(today).Distinct().Order().ToList();
        }

        var instrumentById = instruments.ToDictionary(value => value.Id);
        decimal? HistoricalTradeFx(InvestmentTransaction transaction) =>
            ResolveFx(transaction.Instrument.Currency, appCurrency, transaction.TradeDate, fxBars)?.Rate;
        var points = new List<InvestmentChartPointDto>();
        foreach (var date in dates)
        {
            var relevant = transactions.Where(value => value.TradeDate <= date).ToList();
            var result = accounting.Calculate(relevant, appCurrency, HistoricalTradeFx);
            decimal market = 0;
            decimal cash = 0;
            decimal deposits = 0;
            var complete = true;
            foreach (var position in result.Positions)
            {
                if (!instrumentById.TryGetValue(position.InstrumentId, out var instrument))
                {
                    complete = false;
                    continue;
                }
                if (position.Units == 0) continue;
                var price = ResolvePrices(instrument, priceBars, overrides).LastOrDefault(value => value.Date <= date);
                var fx = ResolveFx(instrument.Currency, appCurrency, date, fxBars);
                if (price is null || fx is null)
                {
                    complete = false;
                    continue;
                }
                market += price.Price * position.Units * fx.Rate;
            }
            var cashNative = new Dictionary<(Guid AccountId, string Currency), decimal>();
            void AddCash(Guid accountId, string currency, decimal amount)
            {
                var key = (accountId, currency);
                cashNative[key] = cashNative.GetValueOrDefault(key) + amount;
            }
            foreach (var flow in cashFlows.Where(value => value.Date <= date))
            {
                AddCash(flow.AccountId, flow.Currency, flow.Amount);
                if (flow.ToCurrency is not null && flow.ToAmount is not null)
                    AddCash(flow.AccountId, flow.ToCurrency, flow.ToAmount.Value);
                // Conversions are value-neutral and never count as deposits.
                if (IsConversion(flow)) continue;
                var flowFx = ResolveFx(flow.Currency, appCurrency, flow.Date, fxBars);
                if (flowFx is null) complete = false;
                else deposits += flow.Amount * flowFx.Rate;
            }
            foreach (var transaction in relevant)
            {
                var currency = transaction.Instrument.Currency;
                var charges = transaction.Fees + transaction.Taxes;
                if (transaction.Type == "Buy") AddCash(transaction.AccountId, currency, -((transaction.CashAmount ?? 0) + charges));
                if (transaction.Type == "Sell" || transaction.Type == "Dividend")
                    AddCash(transaction.AccountId, currency, (transaction.CashAmount ?? 0) - charges);
                if (transaction.Type == "FeeTax") AddCash(transaction.AccountId, currency, -((transaction.CashAmount ?? 0) + charges));
            }
            foreach (var balance in cashNative)
            {
                var cashFx = ResolveFx(balance.Key.Currency, appCurrency, date, fxBars);
                if (cashFx is null) complete = false;
                else cash += balance.Value * cashFx.Rate;
            }
            points.Add(new InvestmentChartPointDto(
                date,
                complete ? market + cash : null,
                complete ? deposits : null));
        }
        return points;
    }

    private static List<ResolvedPrice> ResolvePrices(
        InvestmentInstrument instrument,
        IReadOnlyList<MarketPriceBar> priceBars,
        IReadOnlyList<ManualPriceOverride> overrides)
    {
        var provider = priceBars
            .Where(value =>
                value.Symbol.Equals(instrument.ProviderSymbol, StringComparison.OrdinalIgnoreCase) &&
                value.Mic.Equals(instrument.ProviderMic ?? "", StringComparison.OrdinalIgnoreCase))
            .Select(value => new ResolvedPrice(value.MarketDate, value.Close, value.FetchedAt, false));
        var manual = overrides
            .Where(value => value.InstrumentId == instrument.Id)
            .Select(value => new ResolvedPrice(value.MarketDate, value.Price, value.UpdatedAt, true));
        return provider.Concat(manual)
            .GroupBy(value => value.Date)
            .Select(group => group.OrderByDescending(value => value.Manual).ThenByDescending(value => value.FetchedAt).First())
            .OrderBy(value => value.Date)
            .ToList();
    }

    private static ResolvedFx? ResolveFx(
        string nativeCurrency,
        string appCurrency,
        DateOnly date,
        IReadOnlyList<FxRateBar> fxBars)
    {
        if (nativeCurrency.Equals(appCurrency, StringComparison.OrdinalIgnoreCase))
            return new ResolvedFx(1, date, "Same currency", null);
        var direct = fxBars
            .Where(value =>
                value.BaseCurrency == nativeCurrency &&
                value.QuoteCurrency == appCurrency &&
                value.MarketDate <= date)
            .OrderByDescending(value => value.MarketDate)
            .FirstOrDefault();
        if (direct is not null) return new ResolvedFx(direct.Rate, direct.MarketDate, "Twelve Data direct", direct.FetchedAt);
        var inverse = fxBars.Where(value => value.BaseCurrency == appCurrency &&
                                            value.QuoteCurrency == nativeCurrency &&
                                            value.MarketDate <= date && value.Rate != 0)
            .OrderByDescending(value => value.MarketDate).FirstOrDefault();
        if (inverse is not null)
            return new ResolvedFx(1m / inverse.Rate, inverse.MarketDate, "Twelve Data inverse", inverse.FetchedAt);

        var nativeUsd = ResolveProviderLeg(nativeCurrency, "USD", date, fxBars);
        var usdApp = ResolveProviderLeg("USD", appCurrency, date, fxBars);
        if (nativeUsd is null || usdApp is null) return null;
        return new ResolvedFx(
            nativeUsd.Rate * usdApp.Rate,
            nativeUsd.Date < usdApp.Date ? nativeUsd.Date : usdApp.Date,
            "Twelve Data USD cross",
            MinFetchedAt(nativeUsd.FetchedAt, usdApp.FetchedAt));
    }

    private static ResolvedFx? ResolveProviderLeg(
        string from, string to, DateOnly date, IReadOnlyList<FxRateBar> fxBars)
    {
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) return new ResolvedFx(1, date, "Same currency", null);
        var direct = fxBars.Where(value => value.BaseCurrency == from && value.QuoteCurrency == to &&
                                           value.MarketDate <= date)
            .OrderByDescending(value => value.MarketDate).FirstOrDefault();
        if (direct is not null) return new ResolvedFx(direct.Rate, direct.MarketDate, "Twelve Data direct", direct.FetchedAt);
        var inverse = fxBars.Where(value => value.BaseCurrency == to && value.QuoteCurrency == from &&
                                            value.MarketDate <= date && value.Rate != 0)
            .OrderByDescending(value => value.MarketDate).FirstOrDefault();
        return inverse is null ? null : new ResolvedFx(1m / inverse.Rate, inverse.MarketDate, "Twelve Data inverse", inverse.FetchedAt);
    }

    private static DateTime? MinFetchedAt(DateTime? first, DateTime? second)
        => first is null ? second : second is null ? first : first < second ? first : second;

    private static decimal RoundMoney(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static IReadOnlyList<string> BuildInsights(
        IReadOnlyList<InvestmentHoldingDto> holdings,
        IReadOnlyList<string> warnings)
    {
        if (holdings.Count == 0) return ["Add an opening position or transaction to begin portfolio analysis."];
        var insights = new List<string>();
        var valued = holdings.Where(value => value.ValueApp is not null).ToList();
        if (valued.Count > 0)
        {
            var total = valued.Sum(value => value.ValueApp ?? 0);
            var largest = valued.MaxBy(value => value.ValueApp);
            insights.Add($"Largest holding: {largest!.Symbol} ({(total > 0 ? largest.ValueApp / total * 100 : 0):0.#}% of valued holdings).");
            var performers = valued.Where(value => value.UnrealisedPercent is not null).ToList();
            if (performers.Count > 0)
            {
                insights.Add($"Best performer: {performers.MaxBy(value => value.UnrealisedPercent)!.Symbol}.");
                if (performers.Count > 1) insights.Add($"Weakest performer: {performers.MinBy(value => value.UnrealisedPercent)!.Symbol}.");
            }
            if (total > 0 && (largest.ValueApp ?? 0) / total >= 0.5m)
                insights.Add("Concentration: one holding represents at least half of the valued portfolio.");
        }
        if (holdings.Any(value => value.UsesManualPrice)) insights.Add("Manual prices are currently used for one or more holdings.");
        if (warnings.Any(value => value.Contains("FX", StringComparison.OrdinalIgnoreCase))) insights.Add("Converted totals are incomplete until missing FX rates are supplied.");
        if (holdings.Any(value => value.PriceDate is null || value.PriceDate < DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5))))
            insights.Add("Some holdings have stale or unavailable prices.");
        return insights;
    }

    public static InvestmentTransactionDto ToDto(InvestmentTransaction value, bool isPairedTransfer = false) => new(
        value.Id, value.AccountId, value.InstrumentId, value.Type, value.TradeDate,
        value.Units, value.UnitPrice, value.CashAmount, value.Fees, value.Taxes,
        value.LinkedTransferId, value.CreatedAt,
        isPairedTransfer || value.LinkedTransferId is not null);

    public static ManualPriceDto ToDto(ManualPriceOverride value) => new(
        value.Id, value.InstrumentId, value.MarketDate, value.Price);

    public static InvestmentCashFlowDto ToDto(InvestmentCashFlow value) => new(
        value.Id, value.AccountId, value.Currency, value.Type, value.Amount, value.Date,
        value.ToCurrency, value.ToAmount, value.CreatedAt);

    internal static bool IsConversion(InvestmentCashFlow value)
        => value.Type.Equals("Conversion", StringComparison.OrdinalIgnoreCase);

    private sealed record ResolvedPrice(DateOnly Date, decimal Price, DateTime FetchedAt, bool Manual);
    private sealed record ResolvedFx(decimal Rate, DateOnly Date, string Source, DateTime? FetchedAt);
}
