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
    decimal? AnnualReturn,
    decimal? CashValue,
    decimal? TotalValue,
    // The share of DailyChange the prices moved, carried at the latest rate.
    decimal? DailyPriceChange = null,
    // The share of DailyChange the exchange rate moved. Zero for a single-currency portfolio.
    decimal? DailyCurrencyChange = null);

public sealed record InvestmentCashBalanceDto(
    Guid AccountId,
    string AccountName,
    string Currency,
    decimal Amount,
    decimal? AmountApp);

public sealed record InvestmentContributionDto(DateOnly Date, decimal AmountApp);
public sealed record InvestmentPlanFxRateDto(string Currency, decimal RateToAppCurrency, DateOnly AsOf, string Source);

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
    decimal? RealisedProfitLossApp,
    decimal? NetDividendsApp,
    DateOnly? PriceDate,
    DateTime? PriceFetchedAt,
    bool FxIncomplete,
    decimal? FxRate,
    DateOnly? FxDate,
    DateTime? FxFetchedAt,
    string? FxSource,
    string? PriceSource,
    DateOnly? ValuationAsOf,
    // The share of DailyChangeApp this fund price itself moved, carried at the latest rate.
    decimal? DailyPriceChangeApp = null,
    // The share of DailyChangeApp the exchange rate moved.
    decimal? DailyCurrencyChangeApp = null);

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
    string? ArchiveUnavailableReason,
    MarketInstrumentReference? MarketDataReference);

public sealed record InvestmentPortfolioDto(
    string AppCurrency,
    decimal? ReferenceRate,
    string ReferenceCurrency,
    IReadOnlyList<InvestmentPlanFxRateDto> PlanFxRates,
    InvestmentSummaryDto Summary,
    IReadOnlyList<InvestmentAccountSetupDto> Accounts,
    IReadOnlyList<InvestmentInstrumentSetupDto> Instruments,
    IReadOnlyList<InvestmentHoldingDto> Holdings,
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
    DateTime CreatedAt);

public sealed partial class InvestmentPortfolioService(
    AppDbContext context,
    InvestmentAccountingService accounting,
    IMarketDataProvider provider,
    InvestmentAllocationService? allocationService = null)
{
    private readonly MarketDataSeriesResolver seriesResolver = new(provider.Descriptor);

    private async Task<InvestmentPortfolioDto> BuildPortfolioAsync(
        string range, bool includeChart, CancellationToken cancellationToken)
    {
        var financialSetting = await context.FinancialSettings.AsNoTracking()
            .Select(value => new { value.Currency, value.CycleDay })
            .FirstOrDefaultAsync(cancellationToken);
        var appCurrency = (financialSetting?.Currency ?? "USD")
            .ToUpperInvariant();
        var cycleDay = financialSetting?.CycleDay ?? FinancialConstants.DefaultCycleDay;
        var accounts = await context.InvestmentAccounts.AsNoTracking()
            .OrderBy(value => value.IsArchived).ThenBy(value => value.Name).ThenBy(value => value.Id)
            .ToListAsync(cancellationToken);
        var instruments = await context.InvestmentInstruments.AsNoTracking()
            .OrderBy(value => value.Symbol).ThenBy(value => value.Id)
            .ToListAsync(cancellationToken);
        var transactions = await context.InvestmentTransactions.AsNoTracking()
            .Include(value => value.Instrument)
            .Include(value => value.Account)
            .OrderByDescending(value => value.TradeDate)
            .ThenByDescending(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        var cashFlows = await context.InvestmentCashFlows.AsNoTracking()
            .OrderByDescending(value => value.Date)
            .ThenByDescending(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var (currentCycleYear, currentCycleMonth) =
            CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
        var currentRange = CategoryAttributionService.GetCycleRange(
            currentCycleYear,
            currentCycleMonth,
            cycleDay);
        var currentStart = TransactionDate.StartOfDate(DateOnly.FromDateTime(currentRange.start));
        var snapshotService = new CycleBalanceService(context);
        var openingGrowth = (await snapshotService.GetOpeningBalanceAsync(
            currentCycleYear,
            currentCycleMonth,
            cycleDay,
            cancellationToken)).growth;
        var completedCycles = await context.CycleBalances
            .AsNoTracking()
            .Where(balance => balance.Year < currentCycleYear
                || (balance.Year == currentCycleYear && balance.MonthIndex < currentCycleMonth))
            .OrderBy(balance => balance.Year)
            .ThenBy(balance => balance.MonthIndex)
            .Select(balance => new { balance.Year, balance.MonthIndex, balance.GrowthContributions })
            .ToListAsync(cancellationToken);

        // Snapshots cover the app's 2026+ timeline through the latest closed cycle. Read only the
        // uncached tails: hypothetical pre-baseline imports plus the current/future rows whose
        // values have not reached a closed-cycle snapshot yet. The category prefixes are part of
        // the accounting contract and keep salary splits and structural transfers exact.
        var baselineStart = TransactionDate.StartOfDate(new DateOnly(Math.Min(2026, currentCycleYear), 1, 1));
        var uncachedLedgerTransactions = await context.Transactions.AsNoTracking()
            .Where(value => value.LedgerCategory.ToUpper().StartsWith("GROWTH")
                            || value.LedgerCategory.ToUpper().StartsWith("INCOMESPLIT:")
                            || value.LedgerCategory.ToUpper().StartsWith("TRANSFER:"))
            .Where(value => value.Date < baselineStart || value.Date >= currentStart)
            .OrderBy(value => value.Date)
            .Select(value => new { value.Date, value.Amount, value.LedgerCategory })
            .ToListAsync(cancellationToken);

        var providerId = provider.Descriptor.Id;
        var savedMappings = await context.InvestmentInstrumentMarketMappings.AsNoTracking()
            .Where(value => value.ProviderId == providerId)
            .ToListAsync(cancellationToken);
        var references = instruments
            .Where(value => !value.IsCustom)
            .Select(value => new
            {
                value.Id,
                Reference = savedMappings.FirstOrDefault(mapping => mapping.InvestmentInstrumentId == value.Id) is { } mapping
                    ? new MarketInstrumentReference(mapping.ProviderId, mapping.ExternalInstrumentId)
                    : provider.TryResolveLegacyReference(value.ProviderSymbol, value.ProviderMic)
            })
            .Where(value => value.Reference is not null)
            .ToDictionary(value => value.Id, value => value.Reference!);
        var externalIds = references.Values.Select(value => value.ExternalId).Distinct().ToList();
        var priceBars = externalIds.Count == 0
            ? []
            : await context.MarketPriceBars.AsNoTracking()
                .Where(value => value.Provider == providerId && externalIds.Contains(value.ExternalInstrumentId))
                .ToListAsync(cancellationToken);
        var currencies = instruments.Select(value => value.Currency)
            .Concat(cashFlows.Select(value => value.Currency))
            .Concat(cashFlows.Where(value => value.ToCurrency is not null).Select(value => value.ToCurrency!))
            .Distinct()
            .ToList();
        var fxBars = await context.FxRateBars.AsNoTracking()
            .Where(value => value.Provider == providerId &&
                            (currencies.Contains(value.BaseCurrency) ||
                             currencies.Contains(value.QuoteCurrency) ||
                             value.BaseCurrency == "USD" ||
                             value.QuoteCurrency == "USD"))
            .ToListAsync(cancellationToken);

        // Value foreign-currency dividends, fees, and trades at the market rate on
        // their trade date (stored provider daily close), mirroring how current
        // holdings are valued.
        decimal? HistoricalTradeFx(InvestmentTransaction transaction) =>
            seriesResolver.ResolveFx(transaction.Instrument.Currency, appCurrency, transaction.TradeDate, fxBars)?.Rate;
        var calculation = accounting.Calculate(transactions, appCurrency, HistoricalTradeFx);
        var accountById = accounts.ToDictionary(value => value.Id);
        var instrumentById = instruments.ToDictionary(value => value.Id);
        var holdings = new List<InvestmentHoldingDto>();
        var warnings = calculation.Warnings.ToList();

        foreach (var position in calculation.Positions.Where(value => value.Units != 0))
        {
            if (!accountById.TryGetValue(position.AccountId, out var account) ||
                !instrumentById.TryGetValue(position.InstrumentId, out var instrument))
            {
                continue;
            }

            var prices = seriesResolver.ResolvePrices(references.GetValueOrDefault(instrument.Id), priceBars);
            var latest = prices.LastOrDefault();
            var previous = prices.Count > 1 ? prices[^2] : null;
            var fx = seriesResolver.ResolveFx(instrument.Currency, appCurrency, today, fxBars);
            var previousFx = previous is null
                ? null
                : seriesResolver.ResolveFx(instrument.Currency, appCurrency, previous.Date, fxBars);
            // The move between two price bars has to value both bars at their own date's rate.
            // Today's rate belongs to `valueApp` — what the holding is worth if converted now —
            // but pairing it with the older of two closes measured the price over one bar interval
            // and the currency over however many days separate that close from today. A market
            // holiday, a provider a day behind, or this app's quota-limited refresh was enough to
            // put several days of currency drift into a figure labelled as the latest move, and to
            // make that figure change on a day no new price arrived.
            var latestFx = latest is null
                ? null
                : seriesResolver.ResolveFx(instrument.Currency, appCurrency, latest.Date, fxBars);
            decimal? valueNative = latest is null ? null : latest.Price * position.Units;
            decimal? valueApp = valueNative is not null && fx is not null ? valueNative * fx.Rate : null;
            decimal? unrealised = valueApp is not null && position.CostBasisApp is not null
                ? valueApp - position.CostBasisApp
                : null;
            var canPriceMove = latest is not null && previous is not null
                && latestFx is not null && previousFx is not null;
            // Split exactly, so the two parts always add back to the move: the price leg is the
            // change in price carried at the new rate, the currency leg is the change in rate
            // applied to what the position was already worth. Without it the card states a figure
            // in the app's currency and leaves no way to tell a fall in the shares from a rise in
            // the ringgit — the two can, and regularly do, point in opposite directions.
            decimal? dailyPrice = canPriceMove
                ? (latest!.Price - previous!.Price) * latestFx!.Rate * position.Units
                : null;
            decimal? dailyCurrency = canPriceMove
                ? previous!.Price * (latestFx!.Rate - previousFx!.Rate) * position.Units
                : null;
            decimal? daily = canPriceMove ? dailyPrice + dailyCurrency : null;
            var incomplete = !instrument.Currency.Equals(appCurrency, StringComparison.OrdinalIgnoreCase) && fx is null;
            if (incomplete)
            {
                warnings.Add($"Current FX is missing for {instrument.Currency}/{appCurrency}; converted totals are incomplete.");
            }
            if (previous is not null && previousFx is null)
            {
                warnings.Add($"Previous FX is missing for {instrument.Currency}/{appCurrency}; the latest value move is incomplete.");
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
                position.RealisedApp,
                position.DividendsApp,
                latest?.Date,
                latest?.FetchedAt,
                incomplete,
                fx?.Rate,
                fx?.Date,
                fx?.FetchedAt,
                fx?.Source,
                latest is null ? null : $"{provider.Descriptor.DisplayName} daily close",
                latest is null || fx is null ? null : latest.Date < fx.Date ? latest.Date : fx.Date,
                dailyPrice,
                dailyCurrency));
        }

        // Each summary answers a different question. Missing historical trade FX
        // can make cost or banked profit unavailable without making today's market
        // value unavailable, so do not collapse them behind one completeness flag.
        var openPositions = calculation.Positions.Where(value => value.Units != 0).ToList();
        var marketValueComplete = holdings.All(value => value.ValueApp is not null);
        var costBasisComplete = openPositions.All(value => value.CostBasisApp is not null);
        var realisedComplete = calculation.Positions.All(value => value.RealisedApp is not null);
        var dividendsComplete = calculation.Positions.All(value => value.DividendsApp is not null);
        decimal? marketValue = marketValueComplete ? holdings.Sum(value => value.ValueApp ?? 0) : null;
        decimal? costBasis = costBasisComplete
            ? openPositions.Sum(value => value.CostBasisApp ?? 0)
            : null;
        decimal? realised = realisedComplete ? calculation.Positions.Sum(value => value.RealisedApp ?? 0) : null;
        decimal? dividends = dividendsComplete ? calculation.Positions.Sum(value => value.DividendsApp ?? 0) : null;
        decimal? unrealisedTotal = marketValue is not null && costBasis is not null ? marketValue - costBasis : null;
        var uncachedGrowthAmounts = uncachedLedgerTransactions
            .Select(value => new
            {
                Date = DateOnly.FromDateTime(value.Date),
                // GetCategoryAmount reads only these two fields; the query above deliberately
                // fetches nothing else.
                Amount = CategoryAttributionService.GetCategoryAmount(
                    new Transaction { Amount = value.Amount, LedgerCategory = value.LedgerCategory },
                    "Growth")
            })
            .ToList();
        var growthLedger = openingGrowth + uncachedGrowthAmounts.Sum(value => value.Amount);
        var growthContributions = completedCycles.Sum(value => value.GrowthContributions)
            + uncachedGrowthAmounts.Where(value => value.Amount > 0).Sum(value => value.Amount);

        // Replay the single chronological cash-event contract used by mutation
        // validation so fees, executions, and conversions cannot drift apart.
        var cashByKey = InvestmentHistoryValidationService.ReplayCashEvents(
            InvestmentHistoryValidationService.BuildCashEvents(transactions, cashFlows),
            validateRestrictedDebits: false).Balances;

        decimal? CurrencyFx(string currency) =>
            seriesResolver.ResolveFx(currency, appCurrency, today, fxBars)?.Rate;

        var cashBalances = cashByKey
            .Select(pair =>
            {
                // Zero is zero in every currency and does not require market data.
                // Keeping touched zero balances visible lets the UI distinguish a
                // fully settled currency from one that has no recorded history.
                var fx = pair.Value == 0 ? 0m : CurrencyFx(pair.Key.Currency);
                if (fx is null)
                {
                    warnings.Add($"Current FX is missing for {pair.Key.Currency}/{appCurrency}; cash totals are incomplete.");
                }
                return new InvestmentCashBalanceDto(
                    pair.Key.AccountId,
                    accountById.TryGetValue(pair.Key.AccountId, out var account) ? account.Name : "Account",
                    pair.Key.Currency,
                    pair.Value,
                    fx is null ? null : pair.Value == 0 ? 0m : pair.Value * fx.Value);
            })
            .OrderByDescending(value => value.AmountApp ?? decimal.MinValue)
            .ToList();
        var cashComplete = cashBalances.All(value => value.AmountApp is not null);
        decimal? cashValue = cashComplete ? cashBalances.Sum(value => value.AmountApp ?? 0) : null;
        decimal? totalValue = marketValue is not null && cashValue is not null ? marketValue + cashValue : null;
        var contributionHistory = completedCycles
            .Where(value => value.GrowthContributions > 0m)
            .Select(value => new InvestmentContributionDto(
                DateOnly.FromDateTime(CategoryAttributionService.GetCycleRange(
                    value.Year,
                    value.MonthIndex,
                    cycleDay).start),
                value.GrowthContributions))
            .Concat(uncachedGrowthAmounts
            .Where(value => value.Amount > 0)
            .Select(value => new InvestmentContributionDto(value.Date, value.Amount)))
            .ToList();
        decimal? netDeposits = 0;
        var returnFlows = new List<DatedInvestmentFlow>();
        foreach (var flow in cashFlows)
        {
            // Conversions move value between currencies without adding any, so
            // counting them here would book a phantom contribution.
            if (IsConversion(flow)) continue;
            var fx = seriesResolver.ResolveFx(flow.Currency, appCurrency, flow.Date, fxBars)?.Rate;
            if (fx is null)
            {
                netDeposits = null;
                break;
            }
            var amountApp = flow.Amount * fx.Value;
            netDeposits += amountApp;
            // A deposit is money leaving the investor; a negative withdrawal is
            // money returning to them. Internal trades and income stay inside the
            // terminal portfolio value and therefore are not external return flows.
            returnFlows.Add(new DatedInvestmentFlow(flow.Date, -amountApp));
        }

        var chart = BuildChartIfRequested(
            includeChart, range, transactions, cashFlows, instruments, references, priceBars, fxBars, appCurrency);
        var latestFetchedAt = holdings.Where(value => value.PriceFetchedAt is not null)
            .Select(value => value.PriceFetchedAt)
            .Max();
        var dailyComplete = holdings.Count == 0 || holdings.All(value => value.DailyChangeApp is not null);
        var annualReturn = totalValue is not null && netDeposits is not null
            ? InvestmentReturnCalculator.Calculate(returnFlows.Append(
                new DatedInvestmentFlow(today, totalValue.Value)))
            : null;
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
            dailyComplete ? holdings.Sum(value => value.DailyChangeApp ?? 0) : null,
            annualReturn,
            cashValue,
            totalValue,
            dailyComplete ? holdings.Sum(value => value.DailyPriceChangeApp ?? 0) : null,
            dailyComplete ? holdings.Sum(value => value.DailyCurrencyChangeApp ?? 0) : null);

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
            var hasHistory = transactions.Any(tx => tx.InstrumentId == value.Id);
            var canArchive = !openKeys.Any(key => key.InstrumentId == value.Id);
            return new InvestmentInstrumentSetupDto(
                value.Id, value.Symbol, value.Name, value.Type, value.Exchange, value.Mic, value.Country,
                value.Currency, value.ProviderSymbol, value.ProviderMic, value.IsCustom, value.IsArchived,
                value.AllocationSleeve, value.AllocationOrder,
                !hasHistory, canArchive, canArchive ? null : "Close all units before archiving this investment.",
                references.GetValueOrDefault(value.Id));
        }).ToList();

        var allocation = await (allocationService ?? new InvestmentAllocationService(context)).BuildAsync(
            appCurrency, holdings, instrumentDtos, cashBalances, contributionHistory,
            provider.Descriptor.IsConfigured, cancellationToken);
        var referenceRate = appCurrency.Equals(CurrencyCatalog.ReferenceCurrency, StringComparison.OrdinalIgnoreCase)
            ? 1m
            : seriesResolver.ResolveFx(CurrencyCatalog.ReferenceCurrency, appCurrency, today, fxBars)?.Rate;
        var planFxRates = instruments
            .Where(value => !value.IsArchived && !string.IsNullOrWhiteSpace(value.AllocationSleeve))
            .Select(value => value.Currency.ToUpperInvariant())
            .Append(appCurrency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(currency => (Currency: currency, Fx: seriesResolver.ResolveFx(currency, appCurrency, today, fxBars)))
            .Where(value => value.Fx is not null)
            .Select(value => new InvestmentPlanFxRateDto(
                value.Currency, value.Fx!.Rate, value.Fx.Date, value.Fx.Source))
            .OrderBy(value => value.Currency)
            .ToList();

        return new InvestmentPortfolioDto(
            appCurrency,
            referenceRate,
            CurrencyCatalog.ReferenceCurrency,
            planFxRates,
            summary,
            accountDtos,
            instrumentDtos,
            holdings.OrderByDescending(value => value.ValueApp ?? decimal.MinValue).ToList(),
            chart,
            cashBalances,
            transactions.Count,
            cashFlows.Count,
            BuildInsights(holdings, warnings),
            warnings.Distinct().ToList(),
            latestFetchedAt,
            provider.Descriptor.IsConfigured,
            allocation);
    }

}
