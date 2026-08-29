using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class TransactionPersistenceServiceTests
{
    [Fact]
    public async Task CreateTransactionAsync_RejectsMissingBucketAccountBeforeSaving()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.AddRange(
            new TransactionCategory { Id = "cat-other", Name = "Other" },
            new TransactionCategory { Id = "cat-transfer", Name = "Transfer" });
        await context.SaveChangesAsync();

        var result = await NewService(context).CreateTransactionAsync(
            NewRequest("missing-account", ledgerCategory: "Essentials") with { AccountId = null });

        Assert.Equal(TransactionMutationStatus.InvalidAccount, result.Status);
        Assert.False(await context.Transactions.AnyAsync(transaction => transaction.Id == "missing-account"));
    }

    [Fact]
    public async Task CreateTransactionAsync_RejectsAccountOutsideTheMovedBucket()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "essentials-account",
            Name = "Essentials bank",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
        });
        await context.SaveChangesAsync();

        var result = await NewService(context).CreateTransactionAsync(
            NewRequest("wrong-account", ledgerCategory: "Rewards") with { AccountId = "essentials-account" });

        Assert.Equal(TransactionMutationStatus.InvalidAccount, result.Status);
        Assert.False(await context.Transactions.AnyAsync(transaction => transaction.Id == "wrong-account"));
    }

    [Fact]
    public async Task CreateTransactionAsync_PreservesCounterAccountForCrossBucketTransfersAndAccountMoves()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.LedgerAccounts.AddRange(
            new LedgerAccount
            {
                Id = "essentials-account",
                Name = "Essentials bank",
                Bucket = "Essentials",
                Kind = LedgerAccountKind.Bank,
            },
            new LedgerAccount
            {
                Id = "rewards-account",
                Name = "Rewards wallet",
                Bucket = "Rewards",
                Kind = LedgerAccountKind.EWallet,
            },
            new LedgerAccount
            {
                Id = "cash-account",
                Name = "Cash tin",
                Bucket = "Essentials",
                Kind = LedgerAccountKind.Cash,
            });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var transfer = await service.CreateTransactionAsync(
            NewRequest("cross-transfer", ledgerCategory: "Transfer:Essentials->Rewards", amount: 40m, category: "Transfer") with
            {
                AccountId = "essentials-account",
                CounterAccountId = "rewards-account",
            });
        var move = await service.CreateTransactionAsync(
            NewRequest("account-move", ledgerCategory: "AccountMove", amount: 25m, category: "Transfer") with
            {
                AccountId = "essentials-account",
                CounterAccountId = "cash-account",
            });

        Assert.Equal(TransactionMutationStatus.Created, transfer.Status);
        Assert.Equal(TransactionMutationStatus.Created, move.Status);
        Assert.Equal("rewards-account", transfer.Transaction!.CounterAccountId);
        Assert.True(transfer.Transaction.ExcludeFromAutocomplete);
        Assert.True(move.Transaction!.ExcludeFromAutocomplete);
        Assert.Equal(0m, CategoryAttributionService.GetCategoryAmount(move.Transaction!, "Essentials"));
    }

    [Fact]
    public async Task CreateTransactionAsync_CreatesIncomeSplitsFromSettings()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.FinancialSettings.Add(new FinancialSetting
        {
            EssentialsAlloc = 0.50m,
            GrowthAlloc = 0.25m,
            StabilityAlloc = 0.15m,
            RewardsAlloc = 0.10m,
            CycleDay = 1
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateTransactionAsync(NewRequest("tx-1", ledgerCategory: "Income", amount: 1000m));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal(5, await context.Transactions.CountAsync());
        Assert.True(await context.Transactions.AnyAsync(t => t.Id == "tx-1-split-Essentials" && t.Amount == 500m));
        Assert.All(
            context.Transactions.Where(t => t.Id.StartsWith("tx-1-split-")),
            transaction => Assert.True(transaction.ExcludeFromAutocomplete));
    }

    [Fact]
    public async Task CreateTransactionAsync_PlacesTheMatchingSalaryChildInTheChosenAccount()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "rewards-wallet",
            Name = "Rewards wallet",
            Bucket = "Rewards",
            Kind = LedgerAccountKind.EWallet,
        });
        context.FinancialSettings.Add(new FinancialSetting
        {
            EssentialsAlloc = 0.50m,
            GrowthAlloc = 0.25m,
            StabilityAlloc = 0.15m,
            RewardsAlloc = 0.10m,
        });
        await context.SaveChangesAsync();

        var result = await NewService(context).CreateTransactionAsync(
            NewRequest("salary-account", ledgerCategory: "Income", amount: 1000m) with
            {
                SplitAccountIds = new Dictionary<string, string>
                {
                    ["Essentials"] = "default-essentials",
                    ["Growth"] = "default-growth",
                    ["Stability"] = "default-stability",
                    ["Rewards"] = "rewards-wallet",
                },
            });

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Null(result.Transaction!.AccountId);
        Assert.Equal("rewards-wallet", context.Transactions.Single(row => row.Id == "salary-account-split-Rewards").AccountId);
        Assert.Equal("default-essentials", context.Transactions.Single(row => row.Id == "salary-account-split-Essentials").AccountId);
    }

    [Fact]
    public async Task CreateTransactionAsync_AssignsSplitAccountIdsWhenSpecified()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.LedgerAccounts.AddRange(
            new LedgerAccount { Id = "essentials-custom", Name = "Special checking", Bucket = "Essentials", Kind = LedgerAccountKind.Bank },
            new LedgerAccount { Id = "growth-custom", Name = "Special broker", Bucket = "Growth", Kind = LedgerAccountKind.Bank },
            new LedgerAccount { Id = "rewards-custom", Name = "Special rewards", Bucket = "Rewards", Kind = LedgerAccountKind.EWallet });
        context.FinancialSettings.Add(new FinancialSetting
        {
            EssentialsAlloc = 0.50m,
            GrowthAlloc = 0.25m,
            StabilityAlloc = 0.15m,
            RewardsAlloc = 0.10m,
        });
        await context.SaveChangesAsync();

        var result = await NewService(context).CreateTransactionAsync(
            NewRequest("salary-split-accounts", ledgerCategory: "Income", amount: 1000m) with
            {
                SplitAccountIds = new Dictionary<string, string>
                {
                    ["Essentials"] = "essentials-custom",
                    ["Growth"] = "growth-custom",
                    ["Stability"] = "default-stability",
                    ["Rewards"] = "rewards-custom",
                },
            });

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal("essentials-custom", context.Transactions.Single(row => row.Id == "salary-split-accounts-split-Essentials").AccountId);
        Assert.Equal("growth-custom", context.Transactions.Single(row => row.Id == "salary-split-accounts-split-Growth").AccountId);
        Assert.Equal("default-stability", context.Transactions.Single(row => row.Id == "salary-split-accounts-split-Stability").AccountId);
        Assert.Equal("rewards-custom", context.Transactions.Single(row => row.Id == "salary-split-accounts-split-Rewards").AccountId);
    }

    [Fact]
    public async Task CreateTransactionAsync_IncomeSplitsReconcileExactlyToSalaryAfterRounding()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.FinancialSettings.Add(new FinancialSetting
        {
            EssentialsAlloc = 0.50m,
            GrowthAlloc = 0.25m,
            StabilityAlloc = 0.15m,
            RewardsAlloc = 0.10m,
            CycleDay = 1
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateTransactionAsync(
            NewRequest("salary-with-cents", ledgerCategory: "Income", amount: 1000.05m));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        var splits = await context.Transactions
            .Where(t => t.Id.StartsWith("salary-with-cents-split-"))
            .ToListAsync();
        Assert.Equal(4, splits.Count);
        Assert.Equal(1000.05m, splits.Sum(t => t.Amount));
    }

    [Fact]
    public async Task CreateTransactionAsync_ReturnsExistingForIdempotentPost()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.Transactions.Add(NewTransaction("tx-1"));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateTransactionAsync(NewRequest("tx-1"));

        Assert.Equal(TransactionMutationStatus.Existing, result.Status);
        Assert.Equal("tx-1", result.Transaction!.Id);
    }

    [Fact]
    public async Task CreateTransactionAsync_RejectsMalformedObfuscatedAmount()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = NewService(context);
        var request = new TransactionMutationRequest(
            "tx-invalid",
            "2026-07-09",
            null,
            "Invalid amount",
            "Other",
            "Rewards",
            "not-base64-or-a-number",
            null,
            null);

        var result = await service.CreateTransactionAsync(request);

        Assert.Equal(TransactionMutationStatus.InvalidAmount, result.Status);
        Assert.Empty(context.Transactions);
    }

    [Fact]
    public async Task CreateTransactionAsync_SeparatesCalendarDateFromClientCreationTimestamp()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = NewService(context);
        var postedAt = "2026-07-09T15:00:00.123Z";

        var result = await service.CreateTransactionAsync(NewRequest("tx-timestamp", postedAt: postedAt));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal(new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc), result.Transaction!.Date);
        Assert.Equal(DateTime.Parse(postedAt).ToUniversalTime(), result.Transaction.PostedAt);
    }

    [Fact]
    public async Task UpdateTransactionAsync_RejectsACommitmentCompletionEntry()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        var transaction = NewTransaction("completion-1", amount: -1200m);
        transaction.SavingsGoalId = 7;
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var result = await NewService(context).UpdateTransactionAsync(
            transaction.Id,
            NewRequest(transaction.Id, amount: -1000m));

        Assert.Equal(TransactionMutationStatus.Conflict, result.Status);
        Assert.Equal(-1200m, context.Transactions.Single().Amount);
    }

    [Fact]
    public async Task UpdateTransactionAsync_CannotChangeARecordedRecurringBillLink()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.Transactions.Add(new Transaction
        {
            Id = "recurring-tx",
            Date = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc),
            PostedAt = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc),
            Description = "Recurring payment",
            Category = "Other",
            LedgerCategory = "Rewards",
            Amount = -25m,
            RecurringPaymentId = "bill-original",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 9)
        });
        await context.SaveChangesAsync();

        var request = NewRequest("recurring-tx") with { RecurringPaymentId = "bill-other" };
        var result = await NewService(context).UpdateTransactionAsync("recurring-tx", request);

        Assert.Equal(TransactionMutationStatus.Conflict, result.Status);
        Assert.Equal("bill-original", (await context.Transactions.SingleAsync()).RecurringPaymentId);
    }

    [Fact]
    public async Task UpdateTransactionAsync_CannotChangeARecordedRecurringOccurrenceDate()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.Transactions.Add(new Transaction
        {
            Id = "recurring-date-tx",
            Date = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc),
            PostedAt = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc),
            Description = "Recurring payment",
            Category = "Other",
            LedgerCategory = "Rewards",
            Amount = -25m,
            RecurringPaymentId = "bill-original",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 9)
        });
        await context.SaveChangesAsync();

        var request = NewRequest("recurring-date-tx") with { RecurringOccurrenceDate = "2026-07-10" };
        var result = await NewService(context).UpdateTransactionAsync("recurring-date-tx", request);

        Assert.Equal(TransactionMutationStatus.Conflict, result.Status);
        Assert.Equal(new DateOnly(2026, 7, 9), (await context.Transactions.SingleAsync()).RecurringOccurrenceDate);
    }

    [Theory]
    [InlineData("Transfer:Rewards->Rewards", 25, "Transfer source and target must be different.")]
    [InlineData("Transfer:Rewards->Unknown", 25, "Transfer source and target must be one of")]
    [InlineData("Transfer:Rewards", 25, "must use the format")]
    [InlineData("Transfer:Rewards->Growth", -25, "Transfer amount must be greater than zero.")]
    public async Task CreateTransactionAsync_RejectsInvalidTransferRoutes(
        string ledgerCategory,
        decimal amount,
        string expectedMessage)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateTransactionAsync(
            NewRequest("tx-transfer", ledgerCategory, amount, "Transfer"));

        Assert.Equal(TransactionMutationStatus.InvalidLedgerCategory, result.Status);
        Assert.Contains(expectedMessage, result.Message);
        Assert.Empty(context.Transactions);
    }

    [Fact]
    public async Task CreateTransactionAsync_CanonicalizesValidTransferRoute()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateTransactionAsync(
            NewRequest("tx-transfer", " transfer: rewards -> growth ", 25m, "transfer"));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal("Transfer", result.Transaction!.Category);
        Assert.Equal("Transfer:Rewards->Growth", result.Transaction.LedgerCategory);
    }

    [Fact]
    public async Task CreateTransactionAsync_CanonicalizesDiscardedLedgerCategory()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();

        var result = await NewService(context).CreateTransactionAsync(
            NewRequest("tx-discarded", " discarded ", 0m));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal("Discarded", result.Transaction!.LedgerCategory);
    }

    [Fact]
    public async Task CreateTransactionAsync_RejectsTransferCategoryWithoutRoute()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateTransactionAsync(
            NewRequest("tx-transfer", "Rewards", 25m, "Transfer"));

        Assert.Equal(TransactionMutationStatus.InvalidLedgerCategory, result.Status);
        Assert.Contains("requires a valid", result.Message);
    }

    [Fact]
    public async Task UpdateTransactionAsync_ReplacesExistingSplits()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.Transactions.AddRange(
            NewTransaction("tx-1", ledgerCategory: "IncomeSplit:50,25,15,10", amount: 1000m),
            NewTransaction("tx-1-split-Essentials", ledgerCategory: "Transfer:Income->Essentials", amount: 500m));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.UpdateTransactionAsync(
            "tx-1",
            NewRequest("tx-1", ledgerCategory: "IncomeSplit:25,25,25,25", amount: 800m));

        Assert.Equal(TransactionMutationStatus.Updated, result.Status);
        Assert.Equal(5, await context.Transactions.CountAsync());
        Assert.True(await context.Transactions.AnyAsync(t => t.Id == "tx-1-split-Rewards" && t.Amount == 200m));
    }

    [Fact]
    public async Task DeleteTransactionAsync_ClearsWishlistPurchaseLinkAndDeletesSplits()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.WishlistItems.Add(new WishlistItem
        {
            Id = 1,
            Name = "Camera",
            Price = 100m,
            Priority = "Medium",
            IsPurchased = true,
            PurchaseTransactionId = "tx-1"
        });
        context.Transactions.AddRange(
            NewTransaction("tx-1", wishlistItemId: 1),
            NewTransaction("tx-1-split-Rewards", ledgerCategory: "Transfer:Income->Rewards", amount: 10m));
        context.VaultDocuments.Add(new VaultDocument
        {
            StorageObjectPath = "test-user/2026/document.pdf",
            OriginalFileName = "document.pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            Sha256 = new string('a', 64),
            TaxYear = 2026,
            TransactionId = "tx-1",
            UploadedAt = DateTime.UtcNow,
            RetentionUntil = new DateOnly(2033, 12, 31),
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.DeleteTransactionAsync("tx-1");

        Assert.Equal(TransactionMutationStatus.Deleted, result.Status);
        Assert.Empty(await context.Transactions.ToListAsync());
        var item = (await context.WishlistItems.FindAsync(1))!;
        Assert.False(item.IsPurchased);
        Assert.True(item.IsActive);
        Assert.Null(item.PurchaseTransactionId);
        Assert.Null((await context.VaultDocuments.SingleAsync()).TransactionId);
    }

    [Fact]
    public async Task DeleteTransactionsAsync_DeduplicatesParents_ClearsWishlistAndKeepsVaultDocuments()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.WishlistItems.Add(new WishlistItem
        {
            Id = 1,
            Name = "Camera",
            Price = 100m,
            Priority = "Medium",
            IsPurchased = true,
            PurchaseTransactionId = "tx-1"
        });
        context.Transactions.AddRange(
            NewTransaction("tx-1", wishlistItemId: 1),
            NewTransaction("tx-1-split-Rewards", ledgerCategory: "Transfer:Income->Rewards", amount: 10m),
            NewTransaction("tx-2"));
        context.VaultDocuments.Add(new VaultDocument
        {
            StorageObjectPath = "test-user/2026/document.pdf",
            OriginalFileName = "document.pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            Sha256 = new string('a', 64),
            TaxYear = 2026,
            TransactionId = "tx-1",
            UploadedAt = DateTime.UtcNow,
            RetentionUntil = new DateOnly(2033, 12, 31),
        });
        await context.SaveChangesAsync();

        var result = await NewService(context).DeleteTransactionsAsync(["tx-1-split-Rewards", "tx-1", "tx-1", "tx-2"]);

        Assert.Equal(TransactionMutationStatus.Deleted, result.Status);
        Assert.Equal(2, result.Transactions.Count);
        Assert.Empty(await context.Transactions.ToListAsync());
        var item = (await context.WishlistItems.FindAsync(1))!;
        Assert.False(item.IsPurchased);
        Assert.True(item.IsActive);
        Assert.Null(item.PurchaseTransactionId);
        Assert.Null((await context.VaultDocuments.SingleAsync()).TransactionId);
    }

    [Fact]
    public async Task DeleteTransactionsAsync_RejectsCommitmentWithoutDeletingOtherRows()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var completion = NewTransaction("completion-1");
        completion.SavingsGoalId = 7;
        context.Transactions.AddRange(completion, NewTransaction("tx-2"));
        await context.SaveChangesAsync();

        var result = await NewService(context).DeleteTransactionsAsync(["completion-1", "tx-2"]);

        Assert.Equal(TransactionMutationStatus.Conflict, result.Status);
        Assert.Equal(2, await context.Transactions.CountAsync());
    }

    [Fact]
    public async Task RestoreTransactionsAsync_IsIdempotentAndRegeneratesIncomeSplits()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.FinancialSettings.Add(new FinancialSetting
        {
            EssentialsAlloc = 0.50m,
            GrowthAlloc = 0.25m,
            StabilityAlloc = 0.15m,
            RewardsAlloc = 0.10m,
            CycleDay = 1
        });
        await context.SaveChangesAsync();
        var service = NewService(context);
        var request = NewRequest("salary-restore", ledgerCategory: "Income", amount: 1000m);

        var first = await service.RestoreTransactionsAsync([request]);
        var second = await service.RestoreTransactionsAsync([request]);

        Assert.Equal(TransactionMutationStatus.Created, first.Status);
        Assert.Equal(TransactionMutationStatus.Existing, second.Status);
        Assert.Equal(5, await context.Transactions.CountAsync());
    }

    private static TransactionPersistenceService NewService(
        AppDbContext context,
        FinancialClock? clock = null,
        Services.SavingsGoals.ISharedPoolMutationLock? sharedPoolMutationLock = null)
    {
        var occurrenceService = new RecurringOccurrenceService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RecurringOccurrenceService>.Instance);
        var cycleBalanceService = new CycleBalanceService(context);
        return new TransactionPersistenceService(
            context,
            cycleBalanceService,
            occurrenceService,
            new Services.Stability.StabilityRecoveryService(context, cycleBalanceService, clock),
            clock: clock,
            sharedPoolMutationLock: sharedPoolMutationLock);
    }

    [Fact]
    public async Task Transactions_SerializeEveryMutationThatCanReduceCommitmentBacking()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var mutationLock = new CountingSharedPoolMutationLock();

        var service = NewService(context, sharedPoolMutationLock: mutationLock);
        var result = await service
            .CreateTransactionAsync(NewRequest("reward-spend", ledgerCategory: "Rewards", amount: -25m));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal(
            TransactionMutationStatus.Updated,
            (await service.UpdateTransactionAsync(
                "reward-spend",
                NewRequest("reward-spend", ledgerCategory: "Rewards", amount: -50m))).Status);

        context.Transactions.AddRange(
            NewTransaction("reward-income", ledgerCategory: "Rewards", amount: 100m),
            NewTransaction("essential-income", ledgerCategory: "Essentials", amount: 100m));
        await context.SaveChangesAsync();

        Assert.Equal(TransactionMutationStatus.Deleted, (await service.DeleteTransactionAsync("reward-income")).Status);
        Assert.Equal(
            TransactionMutationStatus.Deleted,
            (await service.DeleteTransactionsAsync(["essential-income"])).Status);

        Assert.Equal(4, mutationLock.AcquisitionCount);
    }

    /// <summary>
    /// The regression for the cap living only on the client: anything that was not the web form --
    /// an AI ledger draft, a recurring settlement, an outbox replay against a stale balance -- sent
    /// plain "Income" and got raw percentages, sailing straight past TargetStabilityFund.
    /// </summary>
    [Fact]
    public async Task CreateTransactionAsync_StopsPlainIncomeAtTheStabilityTarget()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        SeedSettings(context, targetStabilityFund: 1000m);
        // 940 already in the fund, so only 60 of the usual 150 share can land.
        context.Transactions.Add(NewTransaction("seed", ledgerCategory: "Stability", amount: 940m));
        await context.SaveChangesAsync();

        var result = await NewService(context, ClockAt(2026, 7, 9))
            .CreateTransactionAsync(NewRequest("salary", ledgerCategory: "Income", amount: 1000m));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        var stability = await context.Transactions.SingleAsync(t => t.Id == "salary-split-Stability");
        Assert.Equal(60m, stability.Amount);
        // Nothing is lost on the way: the 90 that could not fit still reaches the other buckets.
        var splits = await context.Transactions.Where(t => t.Id.StartsWith("salary-split-")).ToListAsync();
        Assert.Equal(1000m, splits.Sum(t => t.Amount));
    }

    [Fact]
    public async Task CreateTransactionAsync_PlacesRedirectedStabilityOverflowInTheChosenBucketAccount()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        SeedSettings(context, targetStabilityFund: 1000m);
        context.FinancialSettings.Local.Single().StabilityOverflowRedirect =
            StabilityOverflowRedirectOptions.RewardsOnly;
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "rewards-overflow",
            Name = "Rewards overflow",
            Bucket = "Rewards",
            Kind = LedgerAccountKind.EWallet,
        });
        context.Transactions.Add(NewTransaction("seed", ledgerCategory: "Stability", amount: 940m));
        await context.SaveChangesAsync();

        var result = await NewService(context, ClockAt(2026, 7, 9)).CreateTransactionAsync(
            NewRequest("salary-overflow", ledgerCategory: "Income", amount: 1000m) with
            {
                SplitAccountIds = new Dictionary<string, string>
                {
                    ["Essentials"] = "default-essentials",
                    ["Growth"] = "default-growth",
                    ["Stability"] = "default-stability",
                    ["Rewards"] = "rewards-overflow",
                },
            });

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal(
            60m,
            (await context.Transactions.SingleAsync(
                transaction => transaction.Id == "salary-overflow-split-Stability")).Amount);
        var overflow = await context.Transactions.SingleAsync(
            transaction => transaction.Id == "salary-overflow-split-Rewards");
        Assert.Equal(190m, overflow.Amount);
        Assert.Equal("rewards-overflow", overflow.AccountId);
    }

    [Fact]
    public async Task CreateTransactionAsync_SendsNothingToAFundAlreadyAtTarget()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        SeedSettings(context, targetStabilityFund: 1000m);
        context.Transactions.Add(NewTransaction("seed", ledgerCategory: "Stability", amount: 1200m));
        await context.SaveChangesAsync();

        await NewService(context, ClockAt(2026, 7, 9))
            .CreateTransactionAsync(NewRequest("salary", ledgerCategory: "Income", amount: 1000m));

        Assert.False(await context.Transactions.AnyAsync(t => t.Id == "salary-split-Stability"));
        var splits = await context.Transactions.Where(t => t.Id.StartsWith("salary-split-")).ToListAsync();
        Assert.Equal(1000m, splits.Sum(t => t.Amount));
    }

    /// <summary>
    /// An unset target must not read as "cap the fund at nothing" -- that would quietly stop all
    /// emergency-fund funding for anyone who never picked a figure.
    /// </summary>
    [Fact]
    public async Task CreateTransactionAsync_TreatsAZeroTargetAsNoCap()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        SeedSettings(context, targetStabilityFund: 0m);
        await context.SaveChangesAsync();

        await NewService(context, ClockAt(2026, 7, 9))
            .CreateTransactionAsync(NewRequest("salary", ledgerCategory: "Income", amount: 1000m));

        Assert.Equal(150m, (await context.Transactions.SingleAsync(t => t.Id == "salary-split-Stability")).Amount);
    }

    /// <summary>
    /// The offline case. The client proposed against a balance that had moved before the queue
    /// drained; clamping saves the salary where rejecting it would lose an entry nobody can redo.
    /// </summary>
    [Fact]
    public async Task CreateTransactionAsync_ClampsAStaleClientProposalInsteadOfRejectingIt()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        SeedSettings(context, targetStabilityFund: 1000m);
        context.Transactions.Add(NewTransaction("seed", ledgerCategory: "Stability", amount: 950m));
        await context.SaveChangesAsync();

        // A top-up the client believed was affordable: 32% of 1,000 against 50 of real headroom.
        var result = await NewService(context, ClockAt(2026, 7, 9)).CreateTransactionAsync(
            NewRequest("salary", ledgerCategory: "IncomeSplit:40,20,32,8", amount: 1000m));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal(50m, (await context.Transactions.SingleAsync(t => t.Id == "salary-split-Stability")).Amount);
        var splits = await context.Transactions.Where(t => t.Id.StartsWith("salary-split-")).ToListAsync();
        Assert.Equal(1000m, splits.Sum(t => t.Amount));
    }

    [Fact]
    public async Task CreateTransactionAsync_IncomeSplitUsesPlanRevisionAtPostedAt()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        SeedSettings(context, targetStabilityFund: 10000m);
        context.FinancialSettings.Local.Single().StabilityAlloc = 0.10m;
        context.StabilityPlanRevisions.AddRange(
            new StabilityPlanRevision
            {
                EffectiveAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                TargetStabilityFund = 10000m,
                StabilityAlloc = 0.15m,
            },
            new StabilityPlanRevision
            {
                EffectiveAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                TargetStabilityFund = 10000m,
                StabilityAlloc = 0.10m,
            });
        context.Transactions.AddRange(
            NewTransaction("stability-peak", ledgerCategory: "Stability", amount: 2000m),
            NewTransaction("stability-draw", ledgerCategory: "Stability", amount: -500m));
        await context.SaveChangesAsync();

        var request = NewRequest("salary-backdated", ledgerCategory: "IncomeSplit:50,25,24,1", amount: 1000m) with
        {
            Date = "2026-07-15",
            PostedAt = "2026-07-15T12:00:00.000Z"
        };
        var result = await NewService(context, ClockAt(2026, 8, 15)).CreateTransactionAsync(request);

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal(90m, result.Transaction!.StabilityRecoveryTopUpAmount);
        var stabilitySplit = await context.Transactions.SingleAsync(t => t.Id == "salary-backdated-split-Stability");
        Assert.Equal(240m, stabilitySplit.Amount);
        Assert.True(result.Transaction.StabilityRecoveryTopUpAmount <= stabilitySplit.Amount);
    }

    /// <summary>An accepted emergency-fund top-up is just a salary split differently.</summary>
    [Fact]
    public async Task CreateTransactionAsync_HonoursAnAcceptedTopUpAboveTheUsualShare()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        SeedSettings(context, targetStabilityFund: 10000m);
        context.Transactions.AddRange(
            NewTransaction("stability-peak", ledgerCategory: "Stability", amount: 1000m),
            NewTransaction("stability-draw", ledgerCategory: "Stability", amount: -300m));
        await context.SaveChangesAsync();

        var result = await NewService(context, ClockAt(2026, 7, 9)).CreateTransactionAsync(
            NewRequest("salary", ledgerCategory: "Income", amount: 1000m, recoveryTopUp: 90m));

        // 240 rather than the 150 the plain percentage would have delivered.
        Assert.Equal(240m, (await context.Transactions.SingleAsync(t => t.Id == "salary-split-Stability")).Amount);
        Assert.Equal(90m, result.Transaction!.StabilityRecoveryTopUpAmount);
        var splits = await context.Transactions.Where(t => t.Id.StartsWith("salary-split-")).ToListAsync();
        Assert.Equal(1000m, splits.Sum(t => t.Amount));
    }

    [Fact]
    public async Task CreateAndUpdateTransactionAsync_RoundTripTheReloadIntent()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = NewService(context);

        var created = await service.CreateTransactionAsync(
            NewRequest("reload-intent", ledgerCategory: "Stability", reloadIntent: StabilityReloadIntent.Required));

        Assert.Equal(TransactionMutationStatus.Created, created.Status);
        Assert.Equal(StabilityReloadIntent.Required, created.Transaction!.StabilityReloadIntent);

        var updated = await service.UpdateTransactionAsync(
            "reload-intent",
            NewRequest("reload-intent", ledgerCategory: "Stability", reloadIntent: StabilityReloadIntent.NotRequired));

        Assert.Equal(TransactionMutationStatus.Updated, updated.Status);
        Assert.Equal(StabilityReloadIntent.NotRequired, updated.Transaction!.StabilityReloadIntent);
    }

    [Fact]
    public async Task CreateTransactionAsync_DegradesUnknownReloadIntentToUnanswered()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();

        var result = await NewService(context).CreateTransactionAsync(
            NewRequest("unknown-reload-intent", ledgerCategory: "Stability", reloadIntent: "MaybeLater"));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal(StabilityReloadIntent.Unanswered, result.Transaction!.StabilityReloadIntent);
    }

    [Fact]
    public async Task CreateTransactionAsync_CapsTopUpByTheOutstandingObligationNotTheBalanceGap()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        SeedSettings(context, targetStabilityFund: 1000m);
        context.Transactions.AddRange(
            NewTransaction("stability-full", ledgerCategory: "Stability", amount: 1000m),
            NewTransaction("marked-drawdown", ledgerCategory: "Stability", amount: -300m),
            NewTransaction("spent-for-good", ledgerCategory: "Stability", amount: -200m));
        var spent = context.Transactions.Local.Single(t => t.Id == "spent-for-good");
        spent.StabilityReloadIntent = StabilityReloadIntent.NotRequired;
        var marked = context.Transactions.Local.Single(t => t.Id == "marked-drawdown");
        marked.StabilityReloadIntent = StabilityReloadIntent.Required;
        await context.SaveChangesAsync();

        var result = await NewService(context, ClockAt(2026, 7, 9)).CreateTransactionAsync(
            NewRequest("salary-cap", ledgerCategory: "Income", amount: 1000m, recoveryTopUp: 300m));

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal(300m, result.Transaction!.StabilityRecoveryTopUpAmount);
        Assert.Equal(450m, (await context.Transactions.SingleAsync(t => t.Id == "salary-cap-split-Stability")).Amount);
    }

    [Fact]
    public async Task CreateTransactionAsync_PersistsZeroForOrdinarySalary()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        SeedSettings(context, targetStabilityFund: 10000m);
        await context.SaveChangesAsync();

        var result = await NewService(context, ClockAt(2026, 7, 9)).CreateTransactionAsync(
            NewRequest("ordinary-salary", ledgerCategory: "Income", amount: 1000m));

        Assert.Equal(0m, result.Transaction!.StabilityRecoveryTopUpAmount);
    }

    [Fact]
    public async Task UpdateTransactionAsync_OmittedReloadIntentPreservesSpentForGood()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        var transaction = NewTransaction("spent", ledgerCategory: "Stability", amount: -300m);
        transaction.StabilityReloadIntent = StabilityReloadIntent.NotRequired;
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var result = await NewService(context).UpdateTransactionAsync(
            "spent",
            NewRequest("spent", ledgerCategory: "Stability", amount: -250m));

        Assert.Equal(StabilityReloadIntent.NotRequired, result.Transaction!.StabilityReloadIntent);
    }

    /// <summary>
    /// Re-saving a salary is measured against the fund WITHOUT its own earlier contribution.
    /// Counting the old split rows would make the edit look like it had already used up the
    /// headroom it is asking for, and quietly shrink the new one.
    /// </summary>
    [Fact]
    public async Task UpdateTransactionAsync_MeasuresTheCapWithoutTheSalarysOwnPreviousSplit()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        SeedSettings(context, targetStabilityFund: 1000m);
        context.Transactions.AddRange(
            NewTransaction("salary", ledgerCategory: "IncomeSplit:50,25,15,10", amount: 1000m),
            NewTransaction("salary-split-Stability", ledgerCategory: "Transfer:Income->Stability", amount: 150m),
            NewTransaction("seed", ledgerCategory: "Stability", amount: 900m));
        await context.SaveChangesAsync();

        var result = await NewService(context, ClockAt(2026, 7, 9)).UpdateTransactionAsync(
            "salary",
            NewRequest("salary", ledgerCategory: "Income", amount: 1000m));

        Assert.Equal(TransactionMutationStatus.Updated, result.Status);
        // 900 in the fund once the salary's own 150 is set aside, so 100 of room remains -- not the
        // zero that counting its old split row would have implied.
        Assert.Equal(100m, (await context.Transactions.SingleAsync(t => t.Id == "salary-split-Stability")).Amount);
        var splits = await context.Transactions.Where(t => t.Id.StartsWith("salary-split-")).ToListAsync();
        Assert.Equal(1000m, splits.Sum(t => t.Amount));
    }

    [Fact]
    public async Task UpdateTransactionAsync_PreservesAnUnchangedHistoricalIncomeSplit()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        SeedSettings(context, targetStabilityFund: 10000m);
        context.FinancialSettings.Local.Single().StabilityAlloc = 0.30m;
        context.StabilityPlanRevisions.AddRange(
            new StabilityPlanRevision
            {
                EffectiveAt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TargetStabilityFund = 10000m,
                StabilityAlloc = 0.15m,
            },
            new StabilityPlanRevision
            {
                EffectiveAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                TargetStabilityFund = 10000m,
                StabilityAlloc = 0.30m,
            });
        var salary = NewTransaction("historical-salary", ledgerCategory: "Income", amount: 1000m);
        salary.PostedAt = new DateTime(2026, 7, 9, 9, 0, 0, DateTimeKind.Utc);
        salary.StabilityRecoveryTopUpAmount = 0m;
        context.Transactions.AddRange(
            salary,
            NewTransaction("historical-salary-split-Essentials", ledgerCategory: "Transfer:Income->Essentials", amount: 500m),
            NewTransaction("historical-salary-split-Growth", ledgerCategory: "Transfer:Income->Growth", amount: 250m),
            NewTransaction("historical-salary-split-Stability", ledgerCategory: "Transfer:Income->Stability", amount: 150m),
            NewTransaction("historical-salary-split-Rewards", ledgerCategory: "Transfer:Income->Rewards", amount: 100m));
        await context.SaveChangesAsync();

        var result = await NewService(context, ClockAt(2026, 8, 12)).UpdateTransactionAsync(
            salary.Id,
            NewRequest(salary.Id, ledgerCategory: "Income", amount: 1000m));

        Assert.Equal(TransactionMutationStatus.Updated, result.Status);
        Assert.Equal(150m, (await context.Transactions.SingleAsync(t => t.Id == salary.Id + "-split-Stability")).Amount);
        Assert.Equal(1000m, (await context.Transactions
            .Where(t => t.Id.StartsWith(salary.Id + "-split-"))
            .SumAsync(t => t.Amount)));
    }

    [Fact]
    public async Task RestoreTransactionsAsync_UsesThePostedAtPlanForHistoricalIncomeStabilityShare()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        SeedSettings(context, targetStabilityFund: 10000m);
        context.FinancialSettings.Local.Single().StabilityAlloc = 0.30m;
        context.StabilityPlanRevisions.AddRange(
            new StabilityPlanRevision
            {
                EffectiveAt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TargetStabilityFund = 10000m,
                StabilityAlloc = 0.15m,
            },
            new StabilityPlanRevision
            {
                EffectiveAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                TargetStabilityFund = 10000m,
                StabilityAlloc = 0.30m,
            });
        await context.SaveChangesAsync();

        var result = await NewService(context, ClockAt(2026, 8, 12)).RestoreTransactionsAsync([
            NewRequest(
                "restored-historical-salary",
                ledgerCategory: "Income",
                amount: 1000m,
                postedAt: "2026-07-09T09:00:00.000Z")
        ]);

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        Assert.Equal(150m, (await context.Transactions
            .SingleAsync(t => t.Id == "restored-historical-salary-split-Stability")).Amount);
    }

    private static FinancialClock ClockAt(int year, int month, int day)
    {
        return new FinancialClock(
            TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
            new FixedTimeProvider(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static void SeedSettings(AppDbContext context, decimal targetStabilityFund)
    {
        context.FinancialSettings.Add(new FinancialSetting
        {
            EssentialsAlloc = 0.50m,
            GrowthAlloc = 0.25m,
            StabilityAlloc = 0.15m,
            RewardsAlloc = 0.10m,
            TargetStabilityFund = targetStabilityFund,
            StabilityOverflowRedirect = StabilityOverflowRedirectOptions.GrowthRewards,
            CycleDay = 1
        });
    }

    /// <summary>
    /// Deleting a transaction is queued and undoable, so severing its document links must be too.
    /// Restoring the row under the same id -- the single-delete undo, and an offline replay of it
    /// after a reload, which carries no detached-id list of its own -- re-attaches the evidence.
    /// </summary>
    [Fact]
    public async Task CreateTransactionAsync_RelinksDocumentsDetachedByDeletingTheSameTransaction()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.Transactions.Add(NewTransaction("tx-1"));
        context.VaultDocuments.Add(NewVaultDocument("tx-1"));
        await context.SaveChangesAsync();
        var service = NewService(context);

        await service.DeleteTransactionAsync("tx-1");
        var detached = await context.VaultDocuments.SingleAsync();
        Assert.Null(detached.TransactionId);
        Assert.Equal("tx-1", detached.DetachedFromTransactionId);

        var restored = await service.CreateTransactionAsync(NewRequest("tx-1"));

        Assert.Equal(TransactionMutationStatus.Created, restored.Status);
        var relinked = await context.VaultDocuments.SingleAsync();
        Assert.Equal("tx-1", relinked.TransactionId);
        Assert.Null(relinked.DetachedFromTransactionId);
    }

    [Fact]
    public async Task RestoreTransactionsAsync_RelinksDocumentsDetachedByTheBulkDelete()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.Transactions.AddRange(NewTransaction("tx-1"), NewTransaction("tx-2"));
        context.VaultDocuments.AddRange(NewVaultDocument("tx-1"), NewVaultDocument("tx-2"));
        await context.SaveChangesAsync();
        var service = NewService(context);

        await service.DeleteTransactionsAsync(["tx-1", "tx-2"]);
        var result = await service.RestoreTransactionsAsync([NewRequest("tx-1"), NewRequest("tx-2")]);

        Assert.Equal(TransactionMutationStatus.Created, result.Status);
        var documents = await context.VaultDocuments.ToListAsync();
        Assert.Equal(["tx-1", "tx-2"], documents.Select(document => document.TransactionId).Order());
        Assert.All(documents, document => Assert.Null(document.DetachedFromTransactionId));
    }

    /// <summary>
    /// A document the user re-pointed after the delete must stay where they put it, so the marker
    /// only survives until someone states an intent of their own.
    /// </summary>
    [Fact]
    public async Task CreateTransactionAsync_LeavesDocumentsThatWereReattachedElsewhere()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.Transactions.Add(NewTransaction("tx-1"));
        context.VaultDocuments.Add(NewVaultDocument("tx-1"));
        await context.SaveChangesAsync();
        var service = NewService(context);

        await service.DeleteTransactionAsync("tx-1");
        var document = await context.VaultDocuments.SingleAsync();
        document.TransactionId = "tx-9";
        await context.SaveChangesAsync();

        await service.CreateTransactionAsync(NewRequest("tx-1"));

        Assert.Equal("tx-9", (await context.VaultDocuments.SingleAsync()).TransactionId);
    }

    private static VaultDocument NewVaultDocument(string transactionId)
    {
        return new VaultDocument
        {
            StorageObjectPath = $"test-user/2026/{transactionId}.pdf",
            OriginalFileName = "document.pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            Sha256 = new string('a', 64),
            TaxYear = 2026,
            TransactionId = transactionId,
            UploadedAt = DateTime.UtcNow,
            RetentionUntil = new DateOnly(2033, 12, 31),
        };
    }

    private static void SeedCategories(AppDbContext context)
    {
        context.TransactionCategories.AddRange(
            new TransactionCategory { Id = "cat-other", Name = "Other" },
            new TransactionCategory { Id = "cat-transfer", Name = "Transfer" });
        foreach (var bucket in FinancialConstants.BudgetCategories)
        {
            context.LedgerAccounts.Add(new LedgerAccount
            {
                Id = $"default-{bucket.ToLowerInvariant()}",
                Name = $"{bucket} balance",
                Bucket = bucket,
                Kind = LedgerAccountKind.Other,
            });
        }
    }

    private static TransactionMutationRequest NewRequest(
        string id,
        string ledgerCategory = "Rewards",
        decimal amount = -25m,
        string category = "Other",
        string? postedAt = null,
        decimal? recoveryTopUp = null,
        string? reloadIntent = null)
    {
        return new TransactionMutationRequest(
            id,
            "2026-07-09",
            postedAt,
            "Test transaction",
            category,
            ledgerCategory,
            ObfuscationHelper.Obfuscate(amount),
            null,
            null,
            StabilityRecoveryTopUpAmount: recoveryTopUp.HasValue
                ? ObfuscationHelper.Obfuscate(recoveryTopUp.Value)
                : null,
            StabilityReloadIntent: reloadIntent,
            AccountId: ResolveAccountIds(ledgerCategory).AccountId,
            CounterAccountId: ResolveAccountIds(ledgerCategory).CounterAccountId,
            SplitAccountIds: ledgerCategory.Equals("Income", StringComparison.OrdinalIgnoreCase)
                || ledgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase)
                ? FinancialConstants.BudgetCategories.ToDictionary(
                    bucket => bucket,
                    bucket => $"default-{bucket.ToLowerInvariant()}")
                : null);
    }

    private static (string? AccountId, string? CounterAccountId) ResolveAccountIds(string ledgerCategory)
    {
        var normalized = new string(ledgerCategory.Where(character => !char.IsWhiteSpace(character)).ToArray());
        if (FinancialConstants.BudgetCategories.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return ($"default-{normalized.ToLowerInvariant()}", null);
        if (!normalized.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var route = normalized["Transfer:".Length..].Split("->", StringSplitOptions.None);
        if (route.Length != 2) return (null, null);
        var source = route[0];
        var target = route[1];
        return (
            source.Equals("Income", StringComparison.OrdinalIgnoreCase)
                ? $"default-{target.ToLowerInvariant()}"
                : $"default-{source.ToLowerInvariant()}",
            source.Equals("Income", StringComparison.OrdinalIgnoreCase) ? null : $"default-{target.ToLowerInvariant()}");
    }

    private static Transaction NewTransaction(
        string id,
        string ledgerCategory = "Rewards",
        decimal amount = -25m,
        int? wishlistItemId = null)
    {
        return new Transaction
        {
            Id = id,
            Date = DateTime.SpecifyKind(new DateTime(2026, 7, 9), DateTimeKind.Utc),
            Description = "Test transaction",
            Category = "Other",
            LedgerCategory = ledgerCategory,
            Amount = amount,
            AccountId = ResolveAccountIds(ledgerCategory).AccountId,
            CounterAccountId = ResolveAccountIds(ledgerCategory).CounterAccountId,
            WishlistItemId = wishlistItemId
        };
    }

    private sealed class CountingSharedPoolMutationLock : Services.SavingsGoals.ISharedPoolMutationLock
    {
        public int AcquisitionCount { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            AcquisitionCount++;
            return Task.FromResult<IAsyncDisposable>(NoOpLease.Instance);
        }

        private sealed class NoOpLease : IAsyncDisposable
        {
            public static readonly NoOpLease Instance = new();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
