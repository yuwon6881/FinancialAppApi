using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public static class InvestmentKinds
{
    public static readonly HashSet<string> InstrumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stock", "ETF", "MutualFund"
    };

    public static readonly HashSet<string> AllocationSleeves = new(StringComparer.OrdinalIgnoreCase)
    {
        "USEquity", "InternationalExUS", "Bonds"
    };

    public static readonly HashSet<string> TransactionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Buy", "Sell", "Dividend", "FeeTax"
    };

    public static readonly HashSet<string> CashFlowTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Deposit", "Withdrawal", "Conversion"
    };
}

public sealed class InvestmentAccount : IUserOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string UserId { get; set; } = string.Empty;
    [Required, MaxLength(120)] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(3)] public string BaseCurrency { get; set; } = "USD";
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class InvestmentInstrument : IUserOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string UserId { get; set; } = string.Empty;
    [Required, MaxLength(32)] public string Symbol { get; set; } = string.Empty;
    [Required, MaxLength(200)] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(16)] public string Type { get; set; } = "Stock";
    [MaxLength(120)] public string? Exchange { get; set; }
    [MaxLength(8)] public string? Mic { get; set; }
    [MaxLength(80)] public string? Country { get; set; }
    [Required, MaxLength(3)] public string Currency { get; set; } = "USD";
    [MaxLength(32)] public string? ProviderSymbol { get; set; }
    [MaxLength(8)] public string? ProviderMic { get; set; }
    public ICollection<InvestmentInstrumentMarketMapping> MarketMappings { get; set; } = [];
    public bool IsCustom { get; set; }
    public bool IsArchived { get; set; }
    [MaxLength(32)] public string? AllocationSleeve { get; set; }
    public int AllocationOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class InvestmentInstrumentMarketMapping : IUserOwnedEntity
{
    public long Id { get; set; }
    [Required] public string UserId { get; set; } = string.Empty;
    public Guid InvestmentInstrumentId { get; set; }
    public InvestmentInstrument InvestmentInstrument { get; set; } = null!;
    [Required, MaxLength(32)] public string ProviderId { get; set; } = string.Empty;
    [Required, MaxLength(200)] public string ExternalInstrumentId { get; set; } = string.Empty;
    [MaxLength(32)] public string? DisplaySymbol { get; set; }
    [MaxLength(8)] public string? DisplayMic { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class InvestmentPlan : IUserOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string UserId { get; set; } = string.Empty;
    public decimal UsEquityTarget { get; set; } = 66;
    public decimal InternationalExUsTarget { get; set; } = 10;
    public decimal BondsTarget { get; set; } = 24;
    public decimal WatchDrift { get; set; } = 3;
    public decimal AlertDrift { get; set; } = 5;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class InvestmentTransaction : IUserOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string UserId { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
    public InvestmentAccount Account { get; set; } = null!;
    public Guid InstrumentId { get; set; }
    public InvestmentInstrument Instrument { get; set; } = null!;
    [Required, MaxLength(32)] public string Type { get; set; } = "Buy";
    public DateOnly TradeDate { get; set; }
    public decimal Units { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? CashAmount { get; set; }
    public decimal Fees { get; set; }
    public decimal Taxes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// A cash movement into or out of a broker account's uninvested balance
// (settlement cash). Amount is stored signed: deposits positive, withdrawals
// negative. Combined with the implicit cash effects of trades and dividends,
// this gives a per-account, per-currency cash balance that counts toward the
// portfolio's total value -- mirroring a broker app like Moomoo.
//
// A "Conversion" is a currency exchange inside the same account and uses both
// legs: Currency/Amount is the debited side (negative, like a withdrawal) and
// ToCurrency/ToAmount is the credited side (positive). The rate is always
// derivable from ToAmount over the absolute Amount, so it is not stored
// separately. Deposits and withdrawals leave both conversion columns null.
// Because a conversion only moves value between currencies, it must never
// count as a net deposit.
public sealed class InvestmentCashFlow : IUserOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string UserId { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
    public InvestmentAccount Account { get; set; } = null!;
    [Required, MaxLength(3)] public string Currency { get; set; } = "USD";
    [Required, MaxLength(16)] public string Type { get; set; } = "Deposit";
    public decimal Amount { get; set; }
    [MaxLength(3)] public string? ToCurrency { get; set; }
    public decimal? ToAmount { get; set; }
    public DateOnly Date { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class MarketPriceBar
{
    public long Id { get; set; }
    [Required, MaxLength(32)] public string Provider { get; set; } = string.Empty;
    [Required, MaxLength(200)] public string ExternalInstrumentId { get; set; } = string.Empty;
    [Required, MaxLength(32)] public string Symbol { get; set; } = string.Empty;
    [MaxLength(8)] public string Mic { get; set; } = string.Empty;
    public DateOnly MarketDate { get; set; }
    public decimal Close { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

public sealed class FxRateBar
{
    public long Id { get; set; }
    [Required, MaxLength(32)] public string Provider { get; set; } = string.Empty;
    [Required, MaxLength(3)] public string BaseCurrency { get; set; } = string.Empty;
    [Required, MaxLength(3)] public string QuoteCurrency { get; set; } = string.Empty;
    public DateOnly MarketDate { get; set; }
    public decimal Rate { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

public sealed class MarketDataRefreshJob : IUserOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string UserId { get; set; } = string.Empty;
    [Required, MaxLength(24)] public string Status { get; set; } = "Pending";
    [Required, MaxLength(3)] public string ReportingCurrency { get; set; } = "USD";
    [Required, MaxLength(32)] public string ProviderId { get; set; } = string.Empty;
    public int UpdatedItems { get; set; }
    public int TotalItems { get; set; }
    [Required] public string PendingItemsJson { get; set; } = "[]";
    [MaxLength(1000)] public string? Warning { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public sealed class MarketDataQuotaWindow
{
    public long Id { get; set; }
    // Wide enough for the per-user scopes ("user-day:{guid}"), not just the global window names.
    [Required, MaxLength(128)] public string Scope { get; set; } = string.Empty;
    public DateTime WindowStart { get; set; }
    public int Used { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [Timestamp] public uint Version { get; set; }
}

public sealed class InstrumentSearchCache
{
    public long Id { get; set; }
    [Required, MaxLength(32)] public string ProviderId { get; set; } = string.Empty;
    [Required, MaxLength(120)] public string NormalizedQuery { get; set; } = string.Empty;
    [Required] public string ResultsJson { get; set; } = "[]";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
