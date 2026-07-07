using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/financial")]
[AuthorizeToken]
public class FinancialController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;

    private static readonly string[] Months = FinancialConstants.MonthAbbreviations;

    public FinancialController(AppDbContext context, CycleBalanceService cycleBalanceService)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
    }

    private static List<object> ObfuscateTrendPoints(List<(string month, decimal balance)> points) =>
        points.Select(p => (object)new { month = p.month, balance = ObfuscationHelper.Obfuscate(p.balance) }).ToList();

    private static List<object> ObfuscateBreakdown(List<(string category, decimal amount)> breakdown) =>
        breakdown.Select(b => (object)new { category = b.category, amount = ObfuscationHelper.Obfuscate(b.amount) }).ToList();

    // Fetches only one cycle's transactions (indexed by Date), bounding cost to that cycle's row
    // count regardless of total transaction history.
    private async Task<List<Transaction>> GetTransactionsForCycleAsync(int year, int monthIndex, int cycleDay)
    {
        var (start, end, _) = GetCycleRange(year, monthIndex, cycleDay);
        var startDate = DateOnly.FromDateTime(start);
        var endDate = DateOnly.FromDateTime(end);
        return await _context.Transactions
            .Where(t => t.Date >= startDate && t.Date <= endDate)
            .ToListAsync();
    }

    private async Task<FinancialSetting> GetOrCreateSettingAsync()
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null)
        {
            setting = new FinancialSetting();
            _context.FinancialSettings.Add(setting);
        }
        return setting;
    }

    public static async Task<List<object>> GetSubscriptionAlertsAsync(AppDbContext context)
    {
        var setting = await context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null) return new List<object>();
        var cycleDay = setting.CycleDay;

        var activeRecurring = await context.RecurringPayments.Where(r => r.Active).ToListAsync();
        // Confirmed bills are persisted with a freshly generated transaction id (not the
        // "{rp.Id}-{y}-{m}" convention below), so paid-detection has to go through the
        // RecurringPaymentId link rather than an id match.
        var recurringTransactionDates = await context.Transactions
            .Where(t => t.RecurringPaymentId != null)
            .Select(t => new { t.RecurringPaymentId, t.Date })
            .ToListAsync();
        var today = DateTime.Today;
        var (todayMonth, todayYear) = GetCycleMonthAndYearForDate(today, cycleDay);
        var todayMonthIdx = Array.IndexOf(Months, todayMonth) + 1;

        var pending = new List<object>();

        foreach (var rp in activeRecurring)
        {
            if (!DateTime.TryParse(rp.StartDate, out var startDate)) continue;
            DateTime? endDate = null;
            if (!string.IsNullOrEmpty(rp.EndDate) && DateTime.TryParse(rp.EndDate, out var parsedEndDate))
            {
                endDate = parsedEndDate;
            }

            int startYear = Math.Min(2026, startDate.Year);
            for (int y = startYear; y <= todayYear; y++)
            {
                int endMonthIdx = (y == todayYear) ? todayMonthIdx : 12;
                for (int m = 1; m <= endMonthIdx; m++)
                {
                    var (cycleStart, cycleEnd, cycleLabel) = GetCycleRange(y, m, cycleDay);

                    // Find the payment date that falls inside this cycle
                    var billingDate = GetBillingDateForCycle(cycleStart, cycleEnd, cycleDay, rp.DueDate);

                    if (billingDate <= today && billingDate >= startDate && (endDate == null || billingDate <= endDate.Value))
                    {
                        var instanceId = $"{rp.Id}-{y}-{m}";
                        var isPaid = recurringTransactionDates.Any(t =>
                            t.RecurringPaymentId == rp.Id &&
                            t.Date >= DateOnly.FromDateTime(cycleStart) && t.Date <= DateOnly.FromDateTime(cycleEnd));
                        if (!isPaid)
                        {
                            var item = new
                            {
                                id = instanceId,
                                recurringPaymentId = rp.Id,
                                name = rp.Name,
                                amount = ObfuscationHelper.Obfuscate(rp.Amount),
                                category = rp.Category,
                                ledgerCategory = rp.LedgerCategory,
                                billingDate = billingDate.ToString("yyyy-MM-dd"),
                                year = y,
                                month = m,
                                cycleLabel = cycleLabel
                            };

                            pending.Add(item);
                        }
                    }
                }
            }
        }

        return pending;
    }

    private static string GetDayWithSuffix(int day)
    {
        if (day >= 11 && day <= 13) return $"{day}th";
        return (day % 10) switch
        {
            1 => $"{day}st",
            2 => $"{day}nd",
            3 => $"{day}rd",
            _ => $"{day}th"
        };
    }

    public static (DateTime start, DateTime end, string label) GetCycleRange(int year, int monthIndex, int cycleDay)
    {
        if (cycleDay == 1)
        {
            var start = new DateTime(year, monthIndex, 1);
            var end = start.AddMonths(1).AddDays(-1);
            var lbl = $"{start:MMM dd} ~ {end:MMM dd, yyyy}";
            return (start, end, lbl);
        }
        else
        {
            var startDayActual = Math.Min(cycleDay, DateTime.DaysInMonth(year, monthIndex));
            var startDate = new DateTime(year, monthIndex, startDayActual);
            var endDate = startDate.AddMonths(1).AddDays(-1);
            var lbl = $"{startDate.ToString("MMM")} {GetDayWithSuffix(startDate.Day)} ~ {endDate.ToString("MMM")} {GetDayWithSuffix(endDate.Day)}, {startDate.Year}";
            return (startDate, endDate, lbl);
        }
    }

    // Resolves which calendar date a recurring payment's day-of-month due date falls on within
    // a given cycle. Compares against cycleStart.Day (already clamped to the month's length by
    // GetCycleRange) rather than the raw cycleDay setting, so this agrees with GetCycleRange's
    // own month-length clamping for cycle days near the end of a month (29-31).
    private static DateTime GetBillingDateForCycle(DateTime cycleStart, DateTime cycleEnd, int cycleDay, int dueDate)
    {
        if (cycleDay == 1 || dueDate >= cycleStart.Day)
        {
            return new DateTime(cycleStart.Year, cycleStart.Month, Math.Min(dueDate, DateTime.DaysInMonth(cycleStart.Year, cycleStart.Month)));
        }
        return new DateTime(cycleEnd.Year, cycleEnd.Month, Math.Min(dueDate, DateTime.DaysInMonth(cycleEnd.Year, cycleEnd.Month)));
    }

    private static (string month, int year) GetCycleMonthAndYearForDate(DateTime date, int cycleDay)
    {
        var (year, monthIdx) = GetCycleYearAndMonthIndexForDate(DateOnly.FromDateTime(date), cycleDay);
        return (Months[monthIdx - 1], year);
    }

    // Same cycle-membership rule as GetCycleMonthAndYearForDate, but returns the numeric month
    // index directly instead of routing through the month-abbreviation array -- used by callers
    // (CycleBalanceService, transaction mutation invalidation) that key cycles as (year, monthIndex).
    public static (int year, int monthIndex) GetCycleYearAndMonthIndexForDate(DateOnly date, int cycleDay)
    {
        int year = date.Year;
        int monthIdx = date.Month; // 1-indexed

        if (cycleDay > 1 && date.Day < cycleDay)
        {
            monthIdx--;
            if (monthIdx < 1)
            {
                monthIdx = 12;
                year--;
            }
        }

        return (year, monthIdx);
    }

    // GET: api/financial/dashboard
    [HttpGet("dashboard")]
    public async Task<ActionResult<object>> GetDashboardData(
        [FromQuery(Name = "month")] string? queryMonth = null, 
        [FromQuery(Name = "year")] int? queryYear = null)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null)
        {
            var now = DateTime.Now;
            setting = new FinancialSetting
            {
                TargetStabilityFund = 10000.00m,
                SelectedMonth = now.ToString("MMM"),
                SelectedYear = now.Year,
                EssentialsAlloc = 0.50m,
                GrowthAlloc = 0.25m,
                StabilityAlloc = 0.15m,
                RewardsAlloc = 0.10m,
                CycleDay = 28,
                HideSensitive = true
            };
            _context.FinancialSettings.Add(setting);
            await _context.SaveChangesAsync();
        }

        var cycleDay = setting.CycleDay;

        string activeMonth;
        int activeYear;

        if (string.IsNullOrEmpty(queryMonth) || queryYear == null)
        {
            var detected = GetCycleMonthAndYearForDate(DateTime.Now, cycleDay);
            activeMonth = detected.month;
            activeYear = detected.year;

            // Sync detected values back to settings table
            setting.SelectedMonth = activeMonth;
            setting.SelectedYear = activeYear;
            await _context.SaveChangesAsync();
        }
        else
        {
            activeMonth = queryMonth;
            activeYear = queryYear.Value;

            // Sync query parameters back to settings table
            setting.SelectedMonth = activeMonth;
            setting.SelectedYear = activeYear;
            await _context.SaveChangesAsync();
        }

        var allRecurring = await _context.RecurringPayments.ToListAsync();

        var activeMonthIndex = Array.IndexOf(Months, activeMonth) + 1;
        if (activeMonthIndex == 0) activeMonthIndex = 6; // default to June

        var year = activeYear;

        // Active cycle range and its own transactions (bounded to one cycle, indexed by Date)
        var activeRange = GetCycleRange(year, activeMonthIndex, cycleDay);
        var activeRangeStartDate = DateOnly.FromDateTime(activeRange.start);
        var activeRangeEndDate = DateOnly.FromDateTime(activeRange.end);
        var activeCycleTxs = await _context.Transactions
            .Where(t => t.Date >= activeRangeStartDate && t.Date <= activeRangeEndDate)
            .ToListAsync();

        string selectedCycleLabel = activeRange.label;

        // Opening balance = cached ending balance of the cycle immediately before this one
        // (computed/cached on demand -- only the gap since the last cached cycle is replayed).
        var (selectedBudgetEssentials, selectedBudgetGrowth, selectedBudgetStability, selectedBudgetRewards) =
            await _cycleBalanceService.GetOpeningBalanceAsync(year, activeMonthIndex, cycleDay);

        // Net changes for this cycle using LedgerCategory (same attribution rules the cached
        // snapshots use, see FinancialController.GetCategoryAmount)
        var selectedNetEssentials = activeCycleTxs.Sum(t => GetCategoryAmount(t, "Essentials"));
        var selectedNetGrowth = activeCycleTxs.Sum(t => GetCategoryAmount(t, "Growth"));
        var selectedNetStability = activeCycleTxs.Sum(t => GetCategoryAmount(t, "Stability"));
        var selectedNetRewards = activeCycleTxs.Sum(t => GetCategoryAmount(t, "Rewards"));

        var selectedRemEssentials = selectedBudgetEssentials + selectedNetEssentials;
        var selectedRemGrowth = selectedBudgetGrowth + selectedNetGrowth;
        var selectedRemStability = selectedBudgetStability + selectedNetStability;
        var selectedRemRewards = selectedBudgetRewards + selectedNetRewards;

        // Cache this cycle's own ending balance too, then read back every cycle from January of
        // the active year through the active month for the Growth trend line -- all cache hits
        // except for a genuinely new cycle, which the Ensure call above just backfilled.
        await _cycleBalanceService.EnsureComputedThroughAsync(year, activeMonthIndex, cycleDay);
        var trendRows = await _context.CycleBalances
            .Where(b => b.Year == activeYear && b.MonthIndex <= activeMonthIndex)
            .OrderBy(b => b.MonthIndex)
            .ToListAsync();
        var trendPoints = trendRows.Select(r => (Months[r.MonthIndex - 1], r.GrowthBalance)).ToList();

        // Target allocations and budgets (using actual cycle income)
        var selectedCycleIncome = activeCycleTxs
            .Where(t => t.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase) || string.Equals(t.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase))
            .Sum(t => t.Amount);

        var targetEssentials = selectedCycleIncome * setting.EssentialsAlloc;
        var targetGrowth = selectedCycleIncome * setting.GrowthAlloc;
        var targetStability = selectedCycleIncome * setting.StabilityAlloc;
        var targetRewards = selectedCycleIncome * setting.RewardsAlloc;

        var categories = new[]
        {
            new { name = "Essentials", allocation = setting.EssentialsAlloc, target = ObfuscationHelper.Obfuscate(targetEssentials), budget = ObfuscationHelper.Obfuscate(selectedBudgetEssentials), netChange = ObfuscationHelper.Obfuscate(selectedNetEssentials), remaining = ObfuscationHelper.Obfuscate(selectedRemEssentials) },
            new { name = "Growth", allocation = setting.GrowthAlloc, target = ObfuscationHelper.Obfuscate(targetGrowth), budget = ObfuscationHelper.Obfuscate(selectedBudgetGrowth), netChange = ObfuscationHelper.Obfuscate(selectedNetGrowth), remaining = ObfuscationHelper.Obfuscate(selectedRemGrowth) },
            new { name = "Stability", allocation = setting.StabilityAlloc, target = ObfuscationHelper.Obfuscate(targetStability), budget = ObfuscationHelper.Obfuscate(selectedBudgetStability), netChange = ObfuscationHelper.Obfuscate(selectedNetStability), remaining = ObfuscationHelper.Obfuscate(selectedRemStability) },
            new { name = "Rewards", allocation = setting.RewardsAlloc, target = ObfuscationHelper.Obfuscate(targetRewards), budget = ObfuscationHelper.Obfuscate(selectedBudgetRewards), netChange = ObfuscationHelper.Obfuscate(selectedNetRewards), remaining = ObfuscationHelper.Obfuscate(selectedRemRewards) }
        };

        // Monthly Stats calculations
        var totalBalance = selectedRemEssentials + selectedRemStability + selectedRemRewards;

        var monthlyInflow = activeCycleTxs.Where(t => t.Amount > 0 && !t.LedgerCategory.StartsWith("Transfer:")).Sum(t => t.Amount);

        // Outflow (Absolute sum of negative transactions in selected cycle)
        var monthlyOutflow = Math.Abs(activeCycleTxs.Where(t => t.Amount < 0).Sum(t => t.Amount));

        var activeRecurringTotal = Math.Abs(allRecurring.Where(r => r.Active).Sum(r => r.Amount));

        // Metric ratios from Excel formulas
        var growthPercentAchieved = selectedNetGrowth / (targetGrowth > 0 ? targetGrowth : 1m);
        
        // Essentials remaining: calculated based on the actual remaining balance in the Essentials category
        var essentialsPercentRemaining = targetEssentials > 0 
            ? Math.Max(0m, selectedRemEssentials / targetEssentials) 
            : 0m;
            
        var stabilityPercentReached = selectedRemStability / (setting.TargetStabilityFund > 0 ? setting.TargetStabilityFund : 1m);

        // Fetch recent manual transactions for the cycle (max 5)
        var recentTransactions = activeCycleTxs
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .Take(5)
            .Select(t => new
            {
                id = t.Id,
                date = t.Date.ToString("yyyy-MM-dd"),
                description = t.Description,
                category = t.Category,
                ledgerCategory = t.LedgerCategory,
                amount = t.Amount
            })
            .ToList();

        // Calculate all active recurring payments (subscriptions) for this cycle, checking if they are paid
        var activeRecurringList = new List<object>();
        foreach (var rp in allRecurring)
        {
            if (!rp.Active) continue;
            if (!DateTime.TryParse(rp.StartDate, out var rpStartDate)) continue;
            DateTime? rpEndDate = null;
            if (!string.IsNullOrEmpty(rp.EndDate) && DateTime.TryParse(rp.EndDate, out var parsedEndDate))
            {
                rpEndDate = parsedEndDate;
            }

            var billingDate = GetBillingDateForCycle(activeRange.start, activeRange.end, cycleDay, rp.DueDate);

            if (billingDate >= activeRange.start && billingDate <= activeRange.end && billingDate >= rpStartDate && (rpEndDate == null || billingDate <= rpEndDate.Value))
            {
                var instanceId = $"{rp.Id}-{activeYear}-{activeMonthIndex}";
                // FirstOrDefault with no ordering isn't guaranteed stable if more than one
                // transaction ends up matching (e.g. duplicate/manually-backfilled data) -- order
                // deterministically and prefer a real payment over a discard marker so the
                // result can't flip between requests. activeCycleTxs is already scoped to
                // exactly [activeRangeStartDate, activeRangeEndDate], so no extra query is needed.
                var paidTx = activeCycleTxs
                    .Where(t => t.RecurringPaymentId == rp.Id)
                    .OrderBy(t => string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(t => t.Date)
                    .ThenBy(t => t.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                var isDiscarded = paidTx != null && string.Equals(paidTx.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase);
                // A discard-marker transaction still counts as "the cycle's bill was actioned" for
                // matching purposes above, but it is not a real payment -- isPaid must stay false
                // for it so callers that don't also check isDiscarded don't treat it as Paid.
                var isPaid = paidTx != null && !isDiscarded;

                activeRecurringList.Add(new
                {
                    id = instanceId,
                    recurringPaymentId = rp.Id,
                    name = rp.Name,
                    amount = ObfuscationHelper.Obfuscate(Math.Abs(rp.Amount)), // positive on UI
                    category = rp.Category,
                    ledgerCategory = rp.LedgerCategory,
                    dueDate = billingDate.ToString("yyyy-MM-dd"),
                    isPaid = isPaid,
                    isDiscarded = isDiscarded,
                    status = isDiscarded ? "Discarded" : (isPaid ? "Paid" : "Pending"),
                    paidDate = isPaid && !isDiscarded ? paidTx!.Date.ToString("yyyy-MM-dd") : null
                });
            }
        }

        var selectedMonthRecurring = activeRecurringList
            .OrderBy(r => ((dynamic)r).status == "Pending" ? 0 : ((dynamic)r).status == "Paid" ? 1 : 2)
            .ThenBy(r => ((dynamic)r).dueDate)
            .ToList();

        var pendingNotifications = await GetSubscriptionAlertsAsync(_context);

        // Groups outflow transactions by category (falling back to "Other"), summing absolute amounts.
        List<(string category, decimal amount)> BuildBreakdown(List<Transaction> txs) => txs
            .Where(t => t.Amount < 0)
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "Other" : t.Category)
            .Select(g => (category: g.Key, amount: Math.Abs(g.Sum(t => t.Amount))))
            .OrderByDescending(b => b.amount)
            .ToList();

        var monthlyCategoryBreakdown = BuildBreakdown(activeCycleTxs);

        var yearlyTxs = new List<Transaction>();
        for (int m = 1; m <= 12; m++)
        {
            yearlyTxs.AddRange(await GetTransactionsForCycleAsync(activeYear, m, cycleDay));
        }

        var yearlyCategoryBreakdown = BuildBreakdown(yearlyTxs);

        // Helper: collect outflow transactions over a span of N cycles ending at the active cycle
        async Task<List<Transaction>> GetTxsForLastNCycles(int n)
        {
            var result = new List<Transaction>();
            int curMonth = activeMonthIndex;
            int curYear = activeYear;
            for (int i = 0; i < n; i++)
            {
                result.AddRange(await GetTransactionsForCycleAsync(curYear, curMonth, cycleDay));
                curMonth--;
                if (curMonth < 1) { curMonth = 12; curYear--; }
            }
            return result;
        }

        var last3CategoryBreakdown = BuildBreakdown(await GetTxsForLastNCycles(3));
        var last6CategoryBreakdown = BuildBreakdown(await GetTxsForLastNCycles(6));

        // Slice last 3 / last 6 trend points from the accumulated trendPoints list
        var trendPointsList = trendPoints.ToList();
        var last6TrendPoints = trendPointsList.Count >= 6
            ? trendPointsList.GetRange(trendPointsList.Count - 6, 6)
            : trendPointsList.ToList();
        var last3TrendPoints = trendPointsList.Count >= 3
            ? trendPointsList.GetRange(trendPointsList.Count - 3, 3)
            : trendPointsList.ToList();

        var transactionYears = await _context.Transactions.Select(t => t.Date.Year).Distinct().ToListAsync();
        var availableYears = transactionYears
            .Append(DateTime.Now.Year)
            .Distinct()
            .OrderBy(y => y)
            .ToList();

        // Calculate average net additions (inflows) to Rewards category for up to 3 cycles (active cycle + 2 preceding).
        decimal totalPastRewards = 0;
        int activeMonthsCount = 0;
        int tempMonth = activeMonthIndex;
        int tempYear = activeYear;

        for (int i = 0; i < 3; i++)
        {
            var cycleTxsForMonth = await GetTransactionsForCycleAsync(tempYear, tempMonth, cycleDay);

            if (cycleTxsForMonth.Any())
            {
                // Only count positive allocations/inflows to the Rewards category (savings rate capacity)
                var positiveRewards = cycleTxsForMonth
                    .Select(t => GetCategoryAmount(t, "Rewards"))
                    .Where(amt => amt > 0)
                    .Sum();

                totalPastRewards += positiveRewards;
                activeMonthsCount++;
            }

            tempMonth--;
            if (tempMonth < 1)
            {
                tempMonth = 12;
                tempYear--;
            }
        }

        decimal pastThreeMonthsRewardsAverage = 0;
        bool hasRewardsHistory = false;
        if (activeMonthsCount > 0)
        {
            pastThreeMonthsRewardsAverage = totalPastRewards / activeMonthsCount;
            // Only count as having history if the average savings rate is positive
            hasRewardsHistory = pastThreeMonthsRewardsAverage > 0;
        }

        return Ok(new
        {
            setting = new
            {
                targetStabilityFund = ObfuscationHelper.Obfuscate(setting.TargetStabilityFund),
                selectedMonth = setting.SelectedMonth,
                selectedYear = setting.SelectedYear,
                essentialsAlloc = setting.EssentialsAlloc,
                growthAlloc = setting.GrowthAlloc,
                stabilityAlloc = setting.StabilityAlloc,
                rewardsAlloc = setting.RewardsAlloc,
                cycleDay = setting.CycleDay,
                darkMode = setting.DarkMode,
                hideSensitive = setting.HideSensitive,
                currency = setting.Currency,
                stabilityOverflowRedirect = setting.StabilityOverflowRedirect
            },
            cycleLabel = selectedCycleLabel,
            categories,
            stats = new
            {
                totalBalance = ObfuscationHelper.Obfuscate(totalBalance),
                monthlyIncome = ObfuscationHelper.Obfuscate(selectedCycleIncome),
                monthlyInflow = ObfuscationHelper.Obfuscate(monthlyInflow),
                monthlyExpenses = ObfuscationHelper.Obfuscate(monthlyOutflow),
                activeRecurringTotal = ObfuscationHelper.Obfuscate(activeRecurringTotal),
                growthPercentAchieved = (double)Math.Max(0, growthPercentAchieved),
                essentialsPercentRemaining = (double)essentialsPercentRemaining,
                stabilityPercentReached = (double)Math.Max(0, stabilityPercentReached),
                pastThreeMonthsRewardsAverage = ObfuscationHelper.Obfuscate(pastThreeMonthsRewardsAverage),
                hasRewardsHistory = hasRewardsHistory
            },
            recentTransactions = recentTransactions.Select(t => new
            {
                id = t.id,
                date = t.date,
                description = t.description,
                category = t.category,
                ledgerCategory = t.ledgerCategory,
                amount = ObfuscationHelper.Obfuscate(t.amount)
            }).ToList(),
            activeRecurringPayments = selectedMonthRecurring,
            trendPoints = ObfuscateTrendPoints(trendPoints),
            last3TrendPoints = ObfuscateTrendPoints(last3TrendPoints),
            last6TrendPoints = ObfuscateTrendPoints(last6TrendPoints),
            pendingNotifications,
            monthlyCategoryBreakdown = ObfuscateBreakdown(monthlyCategoryBreakdown),
            last3CategoryBreakdown = ObfuscateBreakdown(last3CategoryBreakdown),
            last6CategoryBreakdown = ObfuscateBreakdown(last6CategoryBreakdown),
            yearlyCategoryBreakdown = ObfuscateBreakdown(yearlyCategoryBreakdown),
            availableYears
        });
    }

    // PUT: api/financial/settings
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsDto updateDto)
    {
        var setting = await GetOrCreateSettingAsync();

        setting.TargetStabilityFund = ObfuscationHelper.Deobfuscate(updateDto.TargetStabilityFund);
        setting.EssentialsAlloc = updateDto.EssentialsAlloc;
        setting.GrowthAlloc = updateDto.GrowthAlloc;
        setting.StabilityAlloc = updateDto.StabilityAlloc;
        setting.RewardsAlloc = updateDto.RewardsAlloc;
        // CycleDay=0 (or negative) makes GetCycleRange's `new DateTime(year, month, cycleDay)`
        // throw ArgumentOutOfRangeException on every subsequent dashboard/transactions
        // request, with no self-recovery path via the API. Clamp to a valid day-of-month.
        var newCycleDay = Math.Clamp(updateDto.CycleDay, 1, 31);
        var cycleDayChanged = newCycleDay != setting.CycleDay;
        setting.CycleDay = newCycleDay;
        if (updateDto.DarkMode.HasValue)
        {
            setting.DarkMode = updateDto.DarkMode.Value;
        }
        if (updateDto.HideSensitive.HasValue)
        {
            setting.HideSensitive = updateDto.HideSensitive.Value;
        }
        if (updateDto.VibrationEnabled.HasValue)
        {
            setting.VibrationEnabled = updateDto.VibrationEnabled.Value;
        }
        if (updateDto.StabilityOverflowRedirect != null)
        {
            setting.StabilityOverflowRedirect = updateDto.StabilityOverflowRedirect;
        }
        setting.Currency = updateDto.Currency;

        // Persist the settings change and (if CycleDay changed) wipe the cache atomically -- this
        // app can be signed in on multiple devices, and the cache is shared (not per-device), so
        // without this a concurrent request from another device could land in the gap between
        // "cache wiped" and "new CycleDay committed" and re-cache cycles under the OLD CycleDay,
        // which nothing would later invalidate once the NEW CycleDay takes effect.
        // Must go through CreateExecutionStrategy().ExecuteAsync(...) rather than a bare
        // BeginTransactionAsync() -- Npgsql's EnableRetryOnFailure() retrying execution strategy
        // (Program.cs) refuses to run a user-started transaction directly.
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            if (cycleDayChanged)
            {
                // Every cycle's date boundaries shift retroactively when CycleDay changes, so the
                // entire cached balance history is stale -- not just cycles from today forward.
                await _cycleBalanceService.InvalidateAllAsync();
            }
            await _context.SaveChangesAsync();
            await dbTransaction.CommitAsync();
        });
        return NoContent();
    }

    // PUT: api/financial/dark-mode
    [HttpPut("dark-mode")]
    public async Task<IActionResult> UpdateDarkMode([FromBody] UpdateDarkModeDto dto)
    {
        var setting = await GetOrCreateSettingAsync();

        setting.DarkMode = dto.DarkMode;
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // PUT: api/financial/hide-sensitive
    [HttpPut("hide-sensitive")]
    public async Task<IActionResult> UpdateHideSensitive([FromBody] UpdateHideSensitiveDto dto)
    {
        var setting = await GetOrCreateSettingAsync();

        setting.HideSensitive = dto.HideSensitive;
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // PUT: api/financial/vibration
    [HttpPut("vibration")]
    public async Task<IActionResult> UpdateVibration([FromBody] UpdateVibrationDto dto)
    {
        var setting = await GetOrCreateSettingAsync();

        setting.VibrationEnabled = dto.VibrationEnabled;
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // POST: api/financial/select-period
    [HttpPost("select-period")]
    public async Task<IActionResult> SelectPeriod([FromBody] FinancialSetting periodDto)
    {
        var setting = await GetOrCreateSettingAsync();

        setting.SelectedMonth = periodDto.SelectedMonth;
        setting.SelectedYear = periodDto.SelectedYear;

        await _context.SaveChangesAsync();
        return NoContent();
    }

    // Internal (not private) so CycleBalanceService can reuse the exact same per-transaction
    // bucket-attribution rules when rolling cycle balances forward -- keeps the two call sites
    // (live dashboard math, cached-snapshot backfill) from drifting apart.
    internal static decimal GetCategoryAmount(Transaction t, string categoryName)
    {
        if (string.Equals(t.LedgerCategory, categoryName, StringComparison.OrdinalIgnoreCase))
        {
            return t.Amount;
        }
        if (!string.IsNullOrEmpty(t.LedgerCategory) && t.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = t.LedgerCategory.Substring("IncomeSplit:".Length).Split(',');
            if (parts.Length == 4)
            {
                decimal pct = 0;
                if (categoryName.Equals("Essentials", StringComparison.OrdinalIgnoreCase)) decimal.TryParse(parts[0], out pct);
                else if (categoryName.Equals("Growth", StringComparison.OrdinalIgnoreCase)) decimal.TryParse(parts[1], out pct);
                else if (categoryName.Equals("Stability", StringComparison.OrdinalIgnoreCase)) decimal.TryParse(parts[2], out pct);
                else if (categoryName.Equals("Rewards", StringComparison.OrdinalIgnoreCase)) decimal.TryParse(parts[3], out pct);

                return t.Amount * (pct / 100m);
            }
        }
        if (!string.IsNullOrEmpty(t.LedgerCategory) && t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = t.LedgerCategory.Substring("Transfer:".Length).Split(new[] { "->" }, System.StringSplitOptions.None);
            if (parts.Length == 2)
            {
                var source = parts[0].Trim();
                var target = parts[1].Trim();

                if (string.Equals(categoryName, source, StringComparison.OrdinalIgnoreCase))
                {
                    return -Math.Abs(t.Amount);
                }
                if (string.Equals(categoryName, target, StringComparison.OrdinalIgnoreCase))
                {
                    return Math.Abs(t.Amount);
                }
            }
        }
        return 0;
    }
}

public class UpdateSettingsDto
{
    public string TargetStabilityFund { get; set; } = string.Empty;
    public decimal EssentialsAlloc { get; set; }
    public decimal GrowthAlloc { get; set; }
    public decimal StabilityAlloc { get; set; }
    public decimal RewardsAlloc { get; set; }
    public int CycleDay { get; set; }
    // New property for dark theme preference
    public bool? DarkMode { get; set; }
    public bool? HideSensitive { get; set; }
    public bool? VibrationEnabled { get; set; }
    public string Currency { get; set; } = "USD";
    [System.Text.Json.Serialization.JsonPropertyName("stabilityOverflowRedirect")]
    public string? StabilityOverflowRedirect { get; set; }
}

public class UpdateDarkModeDto
{
    public bool DarkMode { get; set; }
}

public class UpdateHideSensitiveDto
{
    public bool HideSensitive { get; set; }
}

public class UpdateVibrationDto
{
    public bool VibrationEnabled { get; set; }
}
