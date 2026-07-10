using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

// Phase 7: window-based duplicate detection and median/MAD anomaly detection.
public class AiTransactionAnalyticsTests
{
    private static AiAssistantService.AiTransactionRow Row(
        string id, int year, int month, int day, string description, decimal amount,
        string category = "Food", string ledger = "Essentials")
        => new(id, new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Utc),
            $"{year:D4}-{month:D2}-{day:D2}", description, category, ledger, amount);

    // ---------- duplicates ----------

    [Fact]
    public void SameDayExactDuplicate_IsHighConfidence()
    {
        var dups = AiAssistantService.DetectDuplicates(
        [
            Row("a", 2026, 7, 10, "Starbucks", -8),
            Row("b", 2026, 7, 10, "Starbucks", -8)
        ]);

        var candidate = Assert.Single(dups);
        Assert.Equal(0, candidate.DaysApart);
        Assert.True(candidate.Confidence >= 0.85);
        Assert.Equal(["a", "b"], candidate.Ids);
    }

    [Fact]
    public void NextDayDuplicate_IsDetectedWithinWindow()
    {
        var dups = AiAssistantService.DetectDuplicates(
        [
            Row("a", 2026, 7, 10, "Netflix", -15),
            Row("b", 2026, 7, 11, "Netflix", -15)
        ]);

        var candidate = Assert.Single(dups);
        Assert.Equal(1, candidate.DaysApart);
    }

    [Fact]
    public void SameAmountDifferentMerchant_IsNotDuplicate()
    {
        var dups = AiAssistantService.DetectDuplicates(
        [
            Row("a", 2026, 7, 10, "Starbucks", -8),
            Row("b", 2026, 7, 10, "Grocery Mart", -8)
        ]);
        Assert.Empty(dups);
    }

    [Fact]
    public void SameMerchantDifferentAmount_IsNotDuplicate()
    {
        var dups = AiAssistantService.DetectDuplicates(
        [
            Row("a", 2026, 7, 10, "Starbucks", -8),
            Row("b", 2026, 7, 10, "Starbucks", -12)
        ]);
        Assert.Empty(dups);
    }

    [Fact]
    public void MonthlyRecurringChargeOutsideWindow_IsNotDuplicate()
    {
        var dups = AiAssistantService.DetectDuplicates(
        [
            Row("a", 2026, 6, 10, "Netflix", -15),
            Row("b", 2026, 7, 10, "Netflix", -15)
        ]);
        Assert.Empty(dups);
    }

    // ---------- anomalies ----------

    [Fact]
    public void CategoryOutlier_IsFlagged()
    {
        var rows = new List<AiAssistantService.AiTransactionRow>
        {
            Row("1", 2026, 7, 1, "Lunch", -10),
            Row("2", 2026, 7, 2, "Lunch", -12),
            Row("3", 2026, 7, 3, "Lunch", -11),
            Row("4", 2026, 7, 4, "Lunch", -9),
            Row("5", 2026, 7, 5, "Fancy Dinner", -300)
        };

        var anomalies = AiAssistantService.DetectAnomalies(rows);
        var anomaly = Assert.Single(anomalies);
        Assert.Equal("5", anomaly.Id);
    }

    [Fact]
    public void LargestButWithinNormalSpread_IsNotFlagged()
    {
        // Steadily increasing spend; the largest is not a statistical outlier.
        var rows = new List<AiAssistantService.AiTransactionRow>
        {
            Row("1", 2026, 7, 1, "Groceries", -100),
            Row("2", 2026, 7, 2, "Groceries", -110),
            Row("3", 2026, 7, 3, "Groceries", -120),
            Row("4", 2026, 7, 4, "Groceries", -130),
            Row("5", 2026, 7, 5, "Groceries", -140)
        };

        Assert.Empty(AiAssistantService.DetectAnomalies(rows));
    }

    [Fact]
    public void InsufficientHistory_IsNotJudged()
    {
        var rows = new List<AiAssistantService.AiTransactionRow>
        {
            Row("1", 2026, 7, 1, "Lunch", -10),
            Row("2", 2026, 7, 2, "Splurge", -500)
        };
        Assert.Empty(AiAssistantService.DetectAnomalies(rows));
    }

    [Fact]
    public void Transfers_AreIgnoredByBothDetectors()
    {
        var rows = new List<AiAssistantService.AiTransactionRow>
        {
            Row("a", 2026, 7, 10, "Move", -500, category: "Transfer", ledger: "Transfer:Stability"),
            Row("b", 2026, 7, 10, "Move", -500, category: "Transfer", ledger: "Transfer:Stability")
        };
        Assert.Empty(AiAssistantService.DetectDuplicates(rows));
        Assert.Empty(AiAssistantService.DetectAnomalies(rows));
    }
}
