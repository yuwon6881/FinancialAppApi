using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

// Phase 6: typed forecast behavior asserted directly on ComputeWishlistForecast, not on
// serialized prompt strings. Cycles are supplied as already-completed cycles (the service
// excludes the active cycle before calling in), so these tests fix the median/anchor math.
public class WishlistForecasterTests
{
    private const int CycleDay = 1; // cycle == calendar month
    private static readonly DateTime ActiveCycleStart = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly AiAssistantService.CycleKey Apr = new(2026, 4);
    private static readonly AiAssistantService.CycleKey May = new(2026, 5);
    private static readonly AiAssistantService.CycleKey Jun = new(2026, 6);

    private static AiAssistantService.AiTransactionRow Row(int year, int month, decimal amount, string ledger = "Essentials", string category = "Food")
        => new(
            Id: $"{year}-{month}-{amount}-{Guid.NewGuid():N}",
            Timestamp: new DateTime(year, month, 15, 12, 0, 0, DateTimeKind.Utc),
            Date: $"{year:D4}-{month:D2}-15",
            Description: "Item",
            Category: category,
            LedgerCategory: ledger,
            Amount: amount);

    private static AiAssistantService.AiWishlistRow Item(int id, string name, decimal price, bool active = true, bool purchased = false)
        => new(id, name, price, "medium", active, purchased, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static AiAssistantService.WishlistForecastPolicy Policy(
        IReadOnlyList<AiAssistantService.AiWishlistRow> wishlist,
        IReadOnlyList<AiAssistantService.AiTransactionRow> transactions,
        string? reference = null,
        decimal availableFunds = 0m)
        => new(wishlist, transactions, [Apr, May, Jun], CycleDay, ActiveCycleStart, reference, availableFunds);

    [Fact]
    public void PositiveMedian_EstimatesCyclesAndAnchoredDate()
    {
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 100)],
            [Row(2026, 4, 200), Row(2026, 5, 200), Row(2026, 6, 200)]));

        var forecast = Assert.Single(result);
        Assert.Equal(AiAssistantService.WishlistForecastStatus.Estimated, forecast.Status);
        Assert.Equal(200m, forecast.TypicalSavingsPerCycle);
        Assert.Equal(100m, forecast.RemainingAmount);
        Assert.Equal(1, forecast.EstimatedCycles);
        // Anchored to the START OF THE ACTIVE CYCLE (2026-07-01) + 1 cycle, not off cycles[^1].
        Assert.Equal("2026-08-01", forecast.EstimatedDate);
    }

    [Fact]
    public void MixedCycles_MedianIncludesNegativeAndZeroCycles()
    {
        // Net cycle values -50, 0, 100 -> median 0 would be wrong to skip; here median = 0.
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 300)],
            [Row(2026, 4, -50), /* May: nothing -> 0 */ Row(2026, 6, 100)]));

        var forecast = Assert.Single(result);
        // Median of {-50, 0, 100} = 0 -> not currently reachable (old code discarded the bad cycles).
        Assert.Equal(0m, forecast.TypicalSavingsPerCycle);
        Assert.Equal(AiAssistantService.WishlistForecastStatus.NotReachable, forecast.Status);
        Assert.Null(forecast.EstimatedCycles);
    }

    [Fact]
    public void FourCycles_MedianOfMixedValuesMatchesPlanExample()
    {
        // Plan example: net values -50, 0, 100, 200 -> median 50.
        var result = AiAssistantService.ComputeWishlistForecast(new AiAssistantService.WishlistForecastPolicy(
            [Item(1, "Racket", 100)],
            [Row(2026, 3, -50), /* Apr: 0 */ Row(2026, 5, 100), Row(2026, 6, 200)],
            [new AiAssistantService.CycleKey(2026, 3), Apr, May, Jun],
            CycleDay,
            ActiveCycleStart,
            null));

        var forecast = Assert.Single(result);
        Assert.Equal(50m, forecast.TypicalSavingsPerCycle);
        Assert.Equal(2, forecast.EstimatedCycles); // ceil(100/50)
    }

    [Fact]
    public void NegativeMedian_IsNotReachable()
    {
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 100)],
            [Row(2026, 4, -100), Row(2026, 5, -50), Row(2026, 6, -10)]));

        var forecast = Assert.Single(result);
        Assert.Equal(AiAssistantService.WishlistForecastStatus.NotReachable, forecast.Status);
        Assert.Null(forecast.EstimatedCycles);
        Assert.Null(forecast.EstimatedDate);
    }

    [Fact]
    public void TransfersExcludedFromSavings()
    {
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 100)],
            [
                Row(2026, 4, 200), Row(2026, 4, 1000, ledger: "Transfer:Stability"),
                Row(2026, 5, 200), Row(2026, 6, 200)
            ]));

        var forecast = Assert.Single(result);
        Assert.Equal(200m, forecast.TypicalSavingsPerCycle); // transfer ignored
    }

    [Fact]
    public void AvailableFunds_ReduceRemaining()
    {
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 100)],
            [Row(2026, 4, 200), Row(2026, 5, 200), Row(2026, 6, 200)],
            availableFunds: 60m));

        var forecast = Assert.Single(result);
        Assert.Equal(40m, forecast.RemainingAmount);
    }

    [Fact]
    public void TargetAlreadyReached()
    {
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 100)],
            [Row(2026, 4, 200), Row(2026, 5, 200), Row(2026, 6, 200)],
            availableFunds: 150m));

        var forecast = Assert.Single(result);
        Assert.Equal(AiAssistantService.WishlistForecastStatus.AlreadyReached, forecast.Status);
        Assert.Equal(0m, forecast.RemainingAmount);
    }

    [Fact]
    public void MultipleMatches_ReturnsClarification()
    {
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Badminton racket", 100), Item(2, "Tennis racket", 200)],
            [Row(2026, 4, 200)],
            reference: "racket"));

        var forecast = Assert.Single(result);
        Assert.Equal(AiAssistantService.WishlistForecastStatus.MultipleMatches, forecast.Status);
        Assert.NotNull(forecast.Candidates);
        Assert.Equal(2, forecast.Candidates!.Count);
    }

    [Fact]
    public void NoActiveItems_ReturnsEmpty()
    {
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 100, active: false), Item(2, "Bought", 50, purchased: true)],
            [Row(2026, 4, 200)]));

        Assert.Empty(result);
    }

    [Fact]
    public void NoCompletedCycles_ReportsInsufficientData()
    {
        var result = AiAssistantService.ComputeWishlistForecast(new AiAssistantService.WishlistForecastPolicy(
            [Item(1, "Racket", 100)],
            [],
            [],
            CycleDay,
            ActiveCycleStart,
            null));

        var forecast = Assert.Single(result);
        Assert.Equal(AiAssistantService.WishlistForecastStatus.InsufficientData, forecast.Status);
    }
}
