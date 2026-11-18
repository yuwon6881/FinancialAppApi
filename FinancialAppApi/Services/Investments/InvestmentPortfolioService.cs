using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

// Every money figure here is in the app currency at today's rate (constant currency): each
// currency's own figure times one rate, never a rate from the day the money moved.
public sealed record InvestmentSummaryDto(
    decimal GrowthLedgerBalance,
    decimal GrowthContributions,
    // Sent to the broker: external deposits less withdrawals. A conversion inside the broker is not one.
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
    IReadOnlyList<InvestmentCurrencySummaryDto> Currencies);

/// <summary>
/// One currency's part of the portfolio in that currency, with no conversion at all: the honest
/// answer to how the markets priced in it have treated you. Value is unknown only without a price.
/// </summary>
public sealed record InvestmentCurrencySummaryDto(
    string Currency,
    // Today's rate into the app currency; null when none is stored.
    decimal? RateToAppCurrency,
    DateOnly? RateDate,
    decimal? MarketValue,
    decimal CashValue,
    decimal? TotalValue,
    // Money put into this currency: deposits less withdrawals, plus what conversions delivered into
    // it, less what they took out of it.
    decimal NetDeposits,
    decimal CostBasis,
    decimal? UnrealisedProfitLoss,
    decimal? UnrealisedPercent,
    decimal RealisedProfitLoss,
    decimal NetDividends,
    decimal? DailyChange);

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
    decimal CostBasisNative,
    decimal? ValueNative,
    decimal? DailyChangeNative,
    decimal? UnrealisedProfitLossNative,
    decimal RealisedProfitLossNative,
    decimal NetDividendsNative,
    // The *App figures are the native ones at today's rate (FxRate); null without one.
    decimal? ValueApp,
    decimal? DailyChangeApp,
    decimal? UnrealisedProfitLossApp,
    // Native, so a rate never moves it.
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
    InvestmentAllocationService? allocationService = null,
    InvestmentSnapshotService? periodSnapshots = null)
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

        // The opening snapshot includes all closed cycles. Only current and future activity
        // remains to be added without counting historical balances or contributions twice.
        var uncachedLedgerTransactions = await context.Transactions.AsNoTracking()
            .Where(value => value.LedgerCategory.ToUpper().StartsWith("GROWTH")
                            || value.LedgerCategory.ToUpper().StartsWith("INCOMESPLIT:")
                            || value.LedgerCategory.ToUpper().StartsWith("TRANSFER:"))
            .Where(value => value.Date >= currentStart)
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
            .Concat(cashFlows.SelectMany(value => InvestmentConstantCurrency.ContributionLegs(value).Select(leg => leg.Currency)))
            .Append(appCurrency)
            .Append(CurrencyCatalog.ReferenceCurrency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fxBars = await InvestmentConstantCurrency.LatestFxBarsAsync(
            context, providerId, currencies, today, cancellationToken);
        var todayRates = new Dictionary<string, ResolvedMarketFx?>(StringComparer.OrdinalIgnoreCase);
        ResolvedMarketFx? TodayFx(string currency)
        {
            if (!todayRates.TryGetValue(currency, out var fx))
                todayRates[currency] = fx = seriesResolver.ResolveFx(
                    currency, appCurrency, today, fxBars, InvestmentConstantCurrency.RateNeedFrom(today));
            return fx;
        }
        // Zero is zero in every currency, so a settled figure never waits on a rate.
        decimal? ToApp(string currency, decimal? amount)
            => amount is null ? null : amount == 0 ? 0m : TodayFx(currency) is { } fx ? amount * fx.Rate : null;

        var calculation = accounting.Calculate(transactions);
        var accountById = accounts.ToDictionary(value => value.Id);
        var instrumentById = instruments.ToDictionary(value => value.Id);
        var holdings = new List<InvestmentHoldingDto>();
        var warnings = new List<string>();

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
            var fx = TodayFx(instrument.Currency);
            decimal? valueNative = latest is null ? null : latest.Price * position.Units;
            decimal? unrealisedNative = valueNative - position.CostBasisNative;
            // The move between the two latest closes, in the fund's own currency: the price alone.
            decimal? dailyNative = latest is not null && previous is not null
                ? (latest.Price - previous.Price) * position.Units
                : null;
            var incomplete = fx is null;
            if (incomplete)
                warnings.Add($"Today's FX rate is missing for {instrument.Currency}/{appCurrency}; converted totals are incomplete.");

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
                position.CostBasisNative,
                valueNative,
                dailyNative,
                unrealisedNative,
                position.RealisedNative,
                position.DividendsNative,
                ToApp(instrument.Currency, valueNative),
                ToApp(instrument.Currency, dailyNative),
                ToApp(instrument.Currency, unrealisedNative),
                unrealisedNative is not null && position.CostBasisNative > 0
                    ? unrealisedNative / position.CostBasisNative * 100m
                    : null,
                ToApp(instrument.Currency, position.RealisedNative),
                ToApp(instrument.Currency, position.DividendsNative),
                latest?.Date,
                latest?.FetchedAt,
                incomplete,
                fx?.Rate,
                fx?.Date,
                fx?.FetchedAt,
                fx?.Source,
                latest is null ? null : $"{provider.Descriptor.DisplayName} daily close",
                latest is null || fx is null ? null : latest.Date < fx.Date ? latest.Date : fx.Date));
        }

        // Closed positions still carry what they banked and paid out.
        var positionCurrency = calculation.Positions
            .Where(value => instrumentById.ContainsKey(value.InstrumentId))
            .Select(value => (Position: value, Currency: instrumentById[value.InstrumentId].Currency.ToUpperInvariant()))
            .ToList();
        var openPositions = positionCurrency.Where(value => value.Position.Units != 0).ToList();
        decimal? SumApp<T>(IEnumerable<T> rows, Func<T, string> currency, Func<T, decimal?> amount)
            => rows.Aggregate((decimal?)0, (sum, row) => sum + ToApp(currency(row), amount(row)));
        decimal? marketValue = holdings.All(value => value.ValueApp is not null) ? holdings.Sum(value => value.ValueApp ?? 0) : null;
        var costBasis = SumApp(openPositions, value => value.Currency, value => value.Position.CostBasisNative);
        var realised = SumApp(positionCurrency, value => value.Currency, value => value.Position.RealisedNative);
        var dividends = SumApp(positionCurrency, value => value.Currency, value => value.Position.DividendsNative);
        var unrealisedTotal = marketValue - costBasis;
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

        var cashBalances = cashByKey
            .Select(pair =>
            {
                // Keeping touched zero balances visible lets the UI distinguish a
                // fully settled currency from one that has no recorded history.
                var amountApp = ToApp(pair.Key.Currency, pair.Value);
                if (amountApp is null)
                    warnings.Add($"Today's FX rate is missing for {pair.Key.Currency}/{appCurrency}; cash totals are incomplete.");
                return new InvestmentCashBalanceDto(
                    pair.Key.AccountId,
                    accountById.TryGetValue(pair.Key.AccountId, out var account) ? account.Name : "Account",
                    pair.Key.Currency,
                    pair.Value,
                    amountApp);
            })
            .OrderByDescending(value => value.AmountApp ?? decimal.MinValue)
            .ToList();
        decimal? cashValue = cashBalances.All(value => value.AmountApp is not null)
            ? cashBalances.Sum(value => value.AmountApp ?? 0)
            : null;
        var totalValue = marketValue + cashValue;
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

        // Two different questions. What was sent to the broker is the external deposits and
        // withdrawals alone, in the currency each was sent in: an exchange inside the broker sends
        // nothing. What was put into each currency, for performance, also moves money across a
        // conversion at the amounts exchanged. Both are valued at today's rate. A deposit is money
        // leaving the investor and a withdrawal money returning; trades and income stay inside the
        // portfolio's value and are not external flows.
        var netDeposits = SumApp(
            cashFlows.Where(flow => !IsConversion(flow)), flow => flow.Currency.ToUpperInvariant(), flow => flow.Amount);
        var contributionLegs = cashFlows
            .SelectMany(flow => InvestmentConstantCurrency.ContributionLegs(flow).Select(leg => (flow.Date, leg.Currency, leg.Amount)))
            .ToList();
        var annualReturn = CalculateAnnualReturn(contributionLegs, today, totalValue, ToApp);

        var chart = await BuildChartIfRequestedAsync(
            includeChart, range, transactions, cashFlows, instruments, references, priceBars, TodayFx, appCurrency, cancellationToken);
        var latestFetchedAt = holdings.Where(value => value.PriceFetchedAt is not null)
            .Select(value => value.PriceFetchedAt)
            .Max();
        var dailyComplete = holdings.Count == 0 || holdings.All(value => value.DailyChangeApp is not null);
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
            BuildCurrencySummaries(holdings, positionCurrency.Select(value => (value.Position, value.Currency)),
                cashBalances, contributionLegs.Select(value => (value.Currency, value.Amount)), TodayFx));

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
            : TodayFx(CurrencyCatalog.ReferenceCurrency)?.Rate;
        var planFxRates = instruments
            .Where(value => !value.IsArchived && !string.IsNullOrWhiteSpace(value.AllocationSleeve))
            .Select(value => value.Currency.ToUpperInvariant())
            .Append(appCurrency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(currency => (Currency: currency, Fx: TodayFx(currency)))
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
