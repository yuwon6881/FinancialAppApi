using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Controllers;

public partial class InvestmentsController
{
    private async Task<(InvestmentCashFlow? Flow, string? Error)> BuildCashFlowAsync(
        CashFlowMutationDto dto,
        InvestmentCashFlow? existing = null)
    {
        if (!InvestmentKinds.CashFlowTypes.Contains(dto.Type))
            return (null, "Cash flow type must be Deposit, Withdrawal, or Conversion.");
        var account = await context.InvestmentAccounts
            .SingleOrDefaultAsync(value => value.Id == dto.AccountId, HttpContext.RequestAborted);
        if (account is null || account.IsArchived) return (null, "Select an active investment account.");
        if (!ValidCurrency(dto.Currency)) return (null, "Select a supported currency from the list.");
        if (dto.Amount <= 0) return (null, "Enter a positive amount.");
        if (dto.Date == default || dto.Date > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
            return (null, "Enter a valid date.");

        var isConversion = dto.Type.Equals("Conversion", StringComparison.OrdinalIgnoreCase);
        var type = isConversion
            ? "Conversion"
            : dto.Type.Equals("Withdrawal", StringComparison.OrdinalIgnoreCase) ? "Withdrawal" : "Deposit";
        var from = dto.Currency.Trim().ToUpperInvariant();
        string? toCurrency = null;
        decimal? toAmount = null;
        // Store the net effect signed: withdrawals and the sold leg of a
        // conversion reduce the balance.
        var signed = type is "Deposit" ? dto.Amount : -dto.Amount;

        if (isConversion)
        {
            if (string.IsNullOrWhiteSpace(dto.ToCurrency) || !ValidCurrency(dto.ToCurrency))
                return (null, "Select a supported currency to convert into.");
            toCurrency = dto.ToCurrency.Trim().ToUpperInvariant();
            if (toCurrency == from)
                return (null, "A conversion must use two different currencies.");
            if (dto.ToAmount is not > 0)
                return (null, "Enter a positive converted amount.");
            toAmount = dto.ToAmount.Value;
        }

        var flow = existing ?? new InvestmentCashFlow();
        if (existing is null && dto.CreatedAt.HasValue) flow.CreatedAt = dto.CreatedAt.Value.ToUniversalTime();
        flow.AccountId = dto.AccountId;
        flow.Currency = from;
        flow.Type = type;
        flow.Amount = signed;
        flow.ToCurrency = toCurrency;
        flow.ToAmount = toAmount;
        flow.Date = dto.Date;
        if (existing is not null) flow.UpdatedAt = DateTime.UtcNow;
        return (flow, null);
    }

    private async Task<(InvestmentTransaction? Transaction, string? Error)> BuildTransactionAsync(
        InvestmentTransactionMutationDto dto,
        InvestmentTransaction? existing)
    {
        var type = InvestmentKinds.CanonicalTransactionType(dto.Type);
        if (type is null)
            return (null, "Unsupported transaction type.");
        var account = await context.InvestmentAccounts
            .SingleOrDefaultAsync(value => value.Id == dto.AccountId, HttpContext.RequestAborted);
        if (account is null || account.IsArchived) return (null, "Select an active investment account.");
        var instrument = await context.InvestmentInstruments
            .SingleOrDefaultAsync(value => value.Id == dto.InstrumentId, HttpContext.RequestAborted);
        if (instrument is null || instrument.IsArchived) return (null, "Select an active investment.");
        if (dto.TradeDate == default || dto.TradeDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
            return (null, "Enter a valid trade date.");
        if (dto.Fees < 0 || dto.Taxes < 0)
            return (null, "Fees and taxes cannot be negative.");
        decimal units = dto.Units ?? 0;
        decimal? unitPrice = dto.UnitPrice;
        decimal? cash = dto.CashAmount;
        if (type is "Buy" or "Sell")
        {
            var supplied = new[] { dto.Units is > 0, dto.UnitPrice is > 0, dto.CashAmount is > 0 }.Count(value => value);
            if (supplied < 2) return (null, "Enter any two of units, unit price, and gross amount.");
            if (units <= 0) units = cash!.Value / unitPrice!.Value;
            else if (unitPrice is null or <= 0) unitPrice = cash!.Value / units;
            else if (cash is null or <= 0) cash = units * unitPrice.Value;
            var expected = units * unitPrice.Value;
            if (Math.Abs(expected - cash.Value) > Math.Max(0.01m, cash.Value * 0.000001m))
                return (null, "Units, unit price, and gross amount do not agree.");
        }
        if (type == "Dividend" && cash is not > 0)
            return (null, "Enter a positive gross dividend.");
        if (type == "FeeTax" && (cash ?? 0) + dto.Fees + dto.Taxes <= 0)
            return (null, "Enter a positive fee or tax charge.");
        var value = existing ?? new InvestmentTransaction();
        if (existing is null && dto.CreatedAt.HasValue) value.CreatedAt = dto.CreatedAt.Value.ToUniversalTime();
        value.AccountId = dto.AccountId;
        value.InstrumentId = dto.InstrumentId;
        value.Instrument = instrument;
        value.Type = type;
        value.TradeDate = dto.TradeDate;
        value.Units = units;
        value.UnitPrice = unitPrice;
        value.CashAmount = cash;
        value.Fees = dto.Fees;
        value.Taxes = dto.Taxes;
        value.UpdatedAt = DateTime.UtcNow;
        return (value, null);
    }

    internal static string? ValidateTransactionSnapshot(IReadOnlyList<InvestmentTransactionDto> items)
    {
        if (items.Any(item =>
                InvestmentKinds.CanonicalTransactionType(item.Type) is null ||
                item.TradeDate == default ||
                item.TradeDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)) ||
                item.Fees < 0 ||
                item.Taxes < 0))
        {
            return "The activity snapshot contains invalid values.";
        }

        return null;
    }

    internal static string? ValidateCashEvents(
        IEnumerable<(DateOnly Date, DateTime CreatedAt, Guid Id, Guid AccountId, string Currency, decimal Amount)> events)
        => InvestmentHistoryValidationService.ValidateCashEvents(events.Select(value =>
            new InvestmentCashEvent(
                value.Date,
                value.CreatedAt,
                value.Id,
                value.AccountId,
                value.Currency,
                value.Amount)));

    private async Task<string> GetAppCurrencyAsync()
        => (await context.FinancialSettings.AsNoTracking()
            .Select(value => value.Currency)
            .FirstOrDefaultAsync(HttpContext.RequestAborted) ?? "USD").ToUpperInvariant();

    private static string? ValidateAccount(AccountMutationDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Trim().Length > 120) return "Account name is required.";
        if (!ValidCurrency(dto.BaseCurrency)) return "Select a supported currency from the list.";
        return null;
    }

    private string? ValidateInstrument(InstrumentMutationDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Symbol) || dto.Symbol.Trim().Length > 32) return "Symbol is required.";
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Trim().Length > 200) return "Investment name is required.";
        if (!InvestmentKinds.InstrumentTypes.Contains(dto.Type)) return "Type must be Stock, ETF, or Mutual Fund.";
        if (!ValidCurrency(dto.Currency)) return "Select a supported currency from the list.";
        if (dto.Exchange?.Trim().Length > 120 || dto.Mic?.Trim().Length > 8 ||
            dto.Country?.Trim().Length > 80 || dto.ProviderSymbol?.Trim().Length > 32 ||
            dto.ProviderMic?.Trim().Length > 8)
            return "Investment market details are too long.";
        if (dto.MarketDataReference is { } reference &&
            (string.IsNullOrWhiteSpace(reference.ProviderId) || reference.ProviderId.Length > 32 ||
             string.IsNullOrWhiteSpace(reference.ExternalId) || reference.ExternalId.Length > 200))
            return "The market-data reference is invalid.";
        if (dto.MarketDataReference is { } marketReference &&
            !marketReference.ProviderId.Equals(marketDataProvider.Descriptor.Id, StringComparison.OrdinalIgnoreCase))
            return "Choose an investment from the active market-data provider.";
        if (!dto.IsCustom && dto.MarketDataReference is null && string.IsNullOrWhiteSpace(dto.ProviderSymbol))
            return "Provider-backed investments require a market-data reference.";
        return null;
    }

    private async Task<bool> HasHistoricalProviderReferenceChange(Guid instrumentId, InstrumentMutationDto dto)
    {
        if (dto.IsCustom || dto.MarketDataReference is null) return false;
        var existing = await context.InvestmentInstrumentMarketMappings.AsNoTracking()
            .FirstOrDefaultAsync(value =>
                value.InvestmentInstrumentId == instrumentId &&
                value.ProviderId == dto.MarketDataReference.ProviderId,
                HttpContext.RequestAborted);
        return existing is not null &&
               !existing.ExternalInstrumentId.Equals(dto.MarketDataReference.ExternalId, StringComparison.Ordinal);
    }

    private async Task AddOrUpdateMarketMappingAsync(InvestmentInstrument instrument, InstrumentMutationDto dto)
    {
        if (dto.IsCustom) return;
        var reference = dto.MarketDataReference ?? marketDataProvider.TryResolveLegacyReference(
            dto.ProviderSymbol, dto.ProviderMic);
        if (reference is null) return;
        var mapping = context.InvestmentInstrumentMarketMappings.Local.FirstOrDefault(value =>
                          value.InvestmentInstrumentId == instrument.Id && value.ProviderId == reference.ProviderId)
                      ?? await context.InvestmentInstrumentMarketMappings.FirstOrDefaultAsync(value =>
                          value.InvestmentInstrumentId == instrument.Id && value.ProviderId == reference.ProviderId,
                          HttpContext.RequestAborted);
        if (mapping is null)
        {
            context.InvestmentInstrumentMarketMappings.Add(new InvestmentInstrumentMarketMapping
            {
                InvestmentInstrumentId = instrument.Id,
                ProviderId = reference.ProviderId,
                ExternalInstrumentId = reference.ExternalId,
                DisplaySymbol = Clean(dto.ProviderSymbol) ?? dto.Symbol.Trim().ToUpperInvariant(),
                DisplayMic = Clean(dto.ProviderMic)?.ToUpperInvariant()
            });
            return;
        }
        mapping.ExternalInstrumentId = reference.ExternalId;
        mapping.DisplaySymbol = Clean(dto.ProviderSymbol) ?? dto.Symbol.Trim().ToUpperInvariant();
        mapping.DisplayMic = Clean(dto.ProviderMic)?.ToUpperInvariant();
        mapping.UpdatedAt = DateTime.UtcNow;
    }

    internal static bool HasHistoricalMarketIdentityChange(
        InvestmentInstrument existing,
        InstrumentMutationDto updated)
    {
        var providerSymbol = updated.IsCustom ? null : Clean(updated.ProviderSymbol)?.ToUpperInvariant();
        var providerMic = updated.IsCustom ? null : Clean(updated.ProviderMic)?.ToUpperInvariant();
        return !existing.Currency.Equals(updated.Currency.Trim(), StringComparison.OrdinalIgnoreCase) ||
               existing.IsCustom != updated.IsCustom ||
               !string.Equals(existing.ProviderSymbol, providerSymbol, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(existing.ProviderMic, providerMic, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidCurrency(string value)
        => CurrencyCatalog.Contains(value);

    private static bool ValidPage(int page, int pageSize)
        => page > 0 && pageSize is 10 or 25 or 50;

    private async Task<string?> AccountArchiveUnavailableReason(Guid accountId)
    {
        var transactions = await context.InvestmentTransactions.AsNoTracking()
            .Include(value => value.Instrument)
            .Where(value => value.AccountId == accountId)
            .ToListAsync(HttpContext.RequestAborted);
        if (transactions.Count > 0)
        {
            var calculation = accountingService.Calculate(transactions, await GetAppCurrencyAsync());
            if (calculation.Positions.Any(value => value.Units != 0))
                return "Close all positions before archiving this account.";
        }
        var flows = await context.InvestmentCashFlows.AsNoTracking()
            .Where(value => value.AccountId == accountId).ToListAsync(HttpContext.RequestAborted);
        var cash = new Dictionary<string, decimal>();
        foreach (var flow in flows)
        {
            cash[flow.Currency] = cash.GetValueOrDefault(flow.Currency) + flow.Amount;
            // A conversion's credited leg is a separate currency bucket.
            if (flow.ToCurrency is not null && flow.ToAmount is not null)
                cash[flow.ToCurrency] = cash.GetValueOrDefault(flow.ToCurrency) + flow.ToAmount.Value;
        }
        foreach (var transaction in transactions)
        {
            var currency = transaction.Instrument.Currency;
            var effect = transaction.Type switch
            {
                "Buy" => -((transaction.CashAmount ?? 0) + transaction.Fees + transaction.Taxes),
                "Sell" or "Dividend" => (transaction.CashAmount ?? 0) - transaction.Fees - transaction.Taxes,
                "FeeTax" => -((transaction.CashAmount ?? 0) + transaction.Fees + transaction.Taxes),
                _ => 0
            };
            cash[currency] = cash.GetValueOrDefault(currency) + effect;
        }
        return cash.Values.Any(value => value != 0)
            ? "Bring every cash balance to zero before archiving this account."
            : null;
    }

    private static InvestmentInstrument MapInstrument(InstrumentMutationDto dto)
    {
        var value = new InvestmentInstrument();
        ApplyInstrument(value, dto);
        return value;
    }

    private static void ApplyInstrument(InvestmentInstrument value, InstrumentMutationDto dto)
    {
        value.Symbol = dto.Symbol.Trim().ToUpperInvariant();
        value.Name = dto.Name.Trim();
        value.Type = dto.Type.Equals("ETF", StringComparison.OrdinalIgnoreCase)
            ? "ETF"
            : dto.Type.Equals("MutualFund", StringComparison.OrdinalIgnoreCase) ? "MutualFund" : "Stock";
        value.Exchange = Clean(dto.Exchange);
        value.Mic = Clean(dto.Mic)?.ToUpperInvariant();
        value.Country = Clean(dto.Country);
        value.Currency = dto.Currency.Trim().ToUpperInvariant();
        value.ProviderSymbol = dto.IsCustom ? null : Clean(dto.ProviderSymbol)?.ToUpperInvariant();
        value.ProviderMic = dto.IsCustom ? null : Clean(dto.ProviderMic)?.ToUpperInvariant();
        value.IsCustom = dto.IsCustom;
        value.IsArchived = dto.IsArchived;
        value.UpdatedAt = DateTime.UtcNow;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
