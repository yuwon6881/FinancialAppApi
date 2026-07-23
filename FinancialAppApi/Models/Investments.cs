using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public static class InvestmentKinds
{
    public static readonly HashSet<string> InstrumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stock", "ETF"
    };

    public static readonly HashSet<string> TransactionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "OpeningPosition", "Buy", "Sell", "Dividend", "FeeTax", "Split",
        "TransferIn", "TransferOut"
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
    public bool IsCustom { get; set; }
    public bool IsArchived { get; set; }
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
    public decimal? TradeFxRate { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public Guid? LinkedTransferId { get; set; }
    public InvestmentTransaction? LinkedTransfer { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class MarketPriceBar
{
    public long Id { get; set; }
    [Required, MaxLength(32)] public string Provider { get; set; } = "twelvedata";
    [Required, MaxLength(32)] public string Symbol { get; set; } = string.Empty;
    [MaxLength(8)] public string Mic { get; set; } = string.Empty;
    public DateOnly MarketDate { get; set; }
    public decimal Close { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

public sealed class FxRateBar
{
    public long Id { get; set; }
    [Required, MaxLength(32)] public string Provider { get; set; } = "twelvedata";
    [Required, MaxLength(3)] public string BaseCurrency { get; set; } = string.Empty;
    [Required, MaxLength(3)] public string QuoteCurrency { get; set; } = string.Empty;
    public DateOnly MarketDate { get; set; }
    public decimal Rate { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ManualPriceOverride : IUserOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string UserId { get; set; } = string.Empty;
    public Guid InstrumentId { get; set; }
    public InvestmentInstrument Instrument { get; set; } = null!;
    public DateOnly MarketDate { get; set; }
    public decimal Price { get; set; }
    public decimal? FxRate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class MarketDataRefreshJob : IUserOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string UserId { get; set; } = string.Empty;
    [Required, MaxLength(24)] public string Status { get; set; } = "Pending";
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
    [Required, MaxLength(16)] public string Scope { get; set; } = string.Empty;
    public DateTime WindowStart { get; set; }
    public int Used { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [Timestamp] public uint Version { get; set; }
}

public sealed class InstrumentSearchCache
{
    public long Id { get; set; }
    [Required, MaxLength(120)] public string NormalizedQuery { get; set; } = string.Empty;
    [Required] public string ResultsJson { get; set; } = "[]";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
