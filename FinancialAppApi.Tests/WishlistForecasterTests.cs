using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

// Phase 6: typed forecast behavior asserted directly on ComputeWishlistForecast, not on
// serialized prompt strings. The forecaster mirrors the app's Wishlist page: the savings rate
// is the AVERAGE POSITIVE Rewards-ledger attribution over the cycles that had activity, the
// remaining amount is the price minus the current Rewards balance (AvailableFunds), and the
// target date is Today + ceil(months * 30) days. These tests fix that math.
public class WishlistForecasterTests
{
    private const int CycleDay = 1; // cycle == calendar month
    private static readonly DateTime ActiveCycleStart = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Today = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly AiAssistantService.CycleKey Apr = new(2026, 4);
    private static readonly AiAssistantService.CycleKey May = new(2026, 5);
    private static readonly AiAssistantService.CycleKey Jun = new(2026, 6);

    private static AiAssistantService.AiTransactionRow Row(int year, int month, decimal amount, string ledger = "Rewards", string category = "Food")
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
        decimal availableFunds = 0m,
        decimal requiredPerCycle = 0m,
        decimal? budgetedRewardsPerCycle = null)
        => new(wishlist, transactions, [Apr, May, Jun], CycleDay, ActiveCycleStart, Today, reference,
            availableFunds, requiredPerCycle, budgetedRewardsPerCycle);

    [Fact]
    public void PositiveRewardsRate_EstimatesCyclesAndTodayProjectedDate()
    {
        // Rewards saved 200 in each of the 3 cycles -> rate 200. price 100 -> remaining 100.
        // months = 0.5 -> ceil(0.5*30) = 15 days from Today (2026-07-01) = 2026-07-16.
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 100)],
            [Row(2026, 4, 200), Row(2026, 5, 200), Row(2026, 6, 200)]));

        var forecast = Assert.Single(result);
        Assert.Equal(AiAssistantService.WishlistForecastStatus.Estimated, forecast.Status);
        Assert.Equal(200m, forecast.TypicalSavingsPerCycle);
        Assert.Equal(100m, forecast.RemainingAmount);
        Assert.Equal(1, forecast.EstimatedCycles);
        Assert.Equal("2026-07-16", forecast.EstimatedDate);
    }

    [Fact]
    public void OnlyPositiveRewardsCount_NegativeRewardsIgnored()
    {
        // A Rewards outflow (-50) does not reduce the rate; only positive Rewards are summed.
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 100)],
            [Row(2026, 4, 200), Row(2026, 4, -50), Row(2026, 5, 200), Row(2026, 6, 200)]));

        var forecast = Assert.Single(result);
        Assert.Equal(200m, forecast.TypicalSavingsPerCycle);
    }

    [Fact]
    public void EmptyCyclesAreSkipped_NotAveragedAsZero()
    {
        // Apr 200, May no activity, Jun 400 -> averaged over the 2 ACTIVE cycles = 300.
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 600)],
            [Row(2026, 4, 200), Row(2026, 6, 400)]));

        var forecast = Assert.Single(result);
        Assert.Equal(300m, forecast.TypicalSavingsPerCycle);
    }

    [Fact]
    public void TransferIntoRewardsCountsAsSaving()
    {
        // A Transfer:...->Rewards row is routed by GetCategoryAmount into +Rewards, so it saves.
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 100)],
            [
                Row(2026, 4, 100, ledger: "Transfer:Stability->Rewards"),
                Row(2026, 5, 100, ledger: "Transfer:Stability->Rewards"),
                Row(2026, 6, 100, ledger: "Transfer:Stability->Rewards")
            ]));

        var forecast = Assert.Single(result);
        Assert.Equal(100m, forecast.TypicalSavingsPerCycle);
        Assert.Equal(AiAssistantService.WishlistForecastStatus.Estimated, forecast.Status);
    }

    [Fact]
    public void NonRewardsSpendingDoesNotCount_RateZeroIsNotReachable()
    {
        // Cycles have activity but no positive Rewards -> rate 0 -> not reachable.
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 100)],
            [Row(2026, 4, 200, ledger: "Essentials"), Row(2026, 5, 200, ledger: "Essentials"), Row(2026, 6, 200, ledger: "Essentials")]));

        var forecast = Assert.Single(result);
        Assert.Equal(0m, forecast.TypicalSavingsPerCycle);
        Assert.Equal(AiAssistantService.WishlistForecastStatus.NotReachable, forecast.Status);
        Assert.Null(forecast.EstimatedCycles);
        Assert.Null(forecast.EstimatedDate);
    }

    [Fact]
    public void RewardsBalanceReducesRemaining()
    {
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 100)],
            [Row(2026, 4, 200), Row(2026, 5, 200), Row(2026, 6, 200)],
            availableFunds: 60m));

        var forecast = Assert.Single(result);
        Assert.Equal(40m, forecast.RemainingAmount);
    }

    [Fact]
    public void FutureFreeRewardsSubtractsCommitmentPace()
    {
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 300)],
            [Row(2026, 4, 100), Row(2026, 5, 100), Row(2026, 6, 100)],
            availableFunds: 200m,
            requiredPerCycle: 80m));

        var forecast = Assert.Single(result);
        Assert.Equal(20m, forecast.TypicalSavingsPerCycle);
        Assert.Equal(AiAssistantService.WishlistForecastStatus.Estimated, forecast.Status);
        Assert.Equal(5, forecast.EstimatedCycles);
    }

    [Fact]
    public void FreeRewardsMakesTwoHundredAvailableButThreeHundredUnavailable()
    {
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Three Hundred", 300), Item(2, "Two Hundred", 200)],
            [Row(2026, 4, 100), Row(2026, 5, 100), Row(2026, 6, 100)],
            availableFunds: 200m,
            requiredPerCycle: 80m));

        Assert.Equal(AiAssistantService.WishlistForecastStatus.Estimated, result[0].Status);
        Assert.Equal(100m, result[0].RemainingAmount);
        Assert.Equal(AiAssistantService.WishlistForecastStatus.AlreadyReached, result[1].Status);
        Assert.Equal(0m, result[1].RemainingAmount);
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
    public void NoFocusedItem_FallsBackToNewestOpenReward()
    {
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Racket", 100, active: false), Item(2, "Bought", 50, purchased: true)],
            [Row(2026, 4, 200)]));

        var forecast = Assert.Single(result);
        Assert.Equal(1, forecast.WishlistItemId);
    }

    [Fact]
    public void ExplicitReference_CanForecastAnOpenRewardThatIsNotFocused()
    {
        var result = AiAssistantService.ComputeWishlistForecast(Policy(
            [Item(1, "Camera", 500), Item(2, "Weekend trip", 800, active: false)],
            [Row(2026, 4, 200)],
            reference: "Weekend trip"));

        var forecast = Assert.Single(result);
        Assert.Equal(2, forecast.WishlistItemId);
    }

    [Fact]
    public void NoCyclesWithActivity_ReportsInsufficientData()
    {
        var result = AiAssistantService.ComputeWishlistForecast(new AiAssistantService.WishlistForecastPolicy(
            [Item(1, "Racket", 100)],
            [],
            [],
            CycleDay,
            ActiveCycleStart,
            Today,
            null));

        var forecast = Assert.Single(result);
        Assert.Equal(AiAssistantService.WishlistForecastStatus.InsufficientData, forecast.Status);
    }
}
