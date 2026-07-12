namespace FinancialAppApi.Tests.Integration;

public class TimingIntegrationTests : IntegrationTestBase
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public TimingIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async System.Threading.Tasks.Task Measure_Api_Query_Timings()
    {
        // 1. Setup client
        var client = await CreateSignedInClientAsync();

        // Seed some data to make the queries representative
        await Factory.WithDbContextAsync(async db =>
        {
            // Seed a transaction
            db.Transactions.Add(new Models.Transaction
            {
                Id = "tx-timing-1",
                Date = System.DateTime.UtcNow,
                Description = "Starbucks Coffee",
                Category = "Food",
                LedgerCategory = "Essentials",
                Amount = -5.50m
            });
            // Seed a wishlist item
            db.WishlistItems.Add(new Models.WishlistItem
            {
                Id = 1,
                Name = "Nintendo Switch",
                Price = 300m,
                Priority = "Medium",
                IsActive = true,
                IsPurchased = false,
                CreatedAt = System.DateTime.UtcNow
            });
            // Seed a recurring payment
            db.RecurringPayments.Add(new Models.RecurringPayment
            {
                Id = "rec-timing-1",
                Name = "Netflix",
                Amount = 15.99m,
                DueDate = 28,
                NextDueDate = "2026-07-28",
                StartDate = "2026-06-28",
                Active = true,
                Category = "Entertainment",
                LedgerCategory = "Stability",
                Frequency = "Monthly"
            });
            await db.SaveChangesAsync();
        });

        // 2. Measure Dashboard Load
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dashboardRes = await client.GetAsync("/api/financial/dashboard");
        sw.Stop();
        Assert.Equal(System.Net.HttpStatusCode.OK, dashboardRes.StatusCode);
        var dashboardTime = sw.ElapsedMilliseconds;

        // 3. Measure Transaction Page Load
        sw.Restart();
        var transactionsRes = await client.GetAsync("/api/transactions");
        sw.Stop();
        Assert.Equal(System.Net.HttpStatusCode.OK, transactionsRes.StatusCode);
        var transactionsTime = sw.ElapsedMilliseconds;

        // 4. Measure Wishlist List Load
        sw.Restart();
        var wishlistRes = await client.GetAsync("/api/wishlist");
        sw.Stop();
        Assert.Equal(System.Net.HttpStatusCode.OK, wishlistRes.StatusCode);
        var wishlistTime = sw.ElapsedMilliseconds;

        // 5. Measure Recurring Payment List Load
        sw.Restart();
        var recurringRes = await client.GetAsync("/api/recurring-payments");
        sw.Stop();
        Assert.Equal(System.Net.HttpStatusCode.OK, recurringRes.StatusCode);
        var recurringTime = sw.ElapsedMilliseconds;

        // 6. Measure AI Context Load (we call AI chat)
        sw.Restart();
        var aiRes = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, "/api/ai/chat", new { Message = "How much did I spend on coffee?" });
        sw.Stop();
        var aiTime = sw.ElapsedMilliseconds;

        _output.WriteLine("=== API Query Timings ===");
        _output.WriteLine($"- Dashboard load: {dashboardTime} ms");
        _output.WriteLine($"- Transaction page: {transactionsTime} ms");
        _output.WriteLine($"- Wishlist list: {wishlistTime} ms");
        _output.WriteLine($"- Recurring-payment list: {recurringTime} ms");
        _output.WriteLine($"- AI context load: {aiTime} ms");
        _output.WriteLine("=========================");
    }
}
