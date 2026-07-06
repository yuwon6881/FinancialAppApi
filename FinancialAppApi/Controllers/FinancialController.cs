using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/financial")]
[AuthorizeToken]
public class FinancialController : ControllerBase
{
    private readonly AppDbContext _context;

    private static readonly string[] Months = FinancialConstants.MonthAbbreviations;

    public FinancialController(AppDbContext context)
    {
        _context = context;
    }

    private static List<object> ObfuscateTrendPoints(List<(string month, decimal balance)> points) =>
        points.Select(p => (object)new { month = p.month, balance = ObfuscationHelper.Obfuscate(p.balance) }).ToList();

    private static List<object> ObfuscateBreakdown(List<(string category, decimal amount)> breakdown) =>
        breakdown.Select(b => (object)new { category = b.category, amount = ObfuscationHelper.Obfuscate(b.amount) }).ToList();

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
                            DateTime.TryParse(t.Date, out var paidTxDate) &&
                            paidTxDate >= cycleStart && paidTxDate <= cycleEnd);
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

        return (Months[monthIdx - 1], year);
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

        var allTransactions = await _context.Transactions.ToListAsync();
        var allRecurring = await _context.RecurringPayments.ToListAsync();

        var activeMonthIndex = Array.IndexOf(Months, activeMonth) + 1;
        if (activeMonthIndex == 0) activeMonthIndex = 6; // default to June

        var year = activeYear;

        // Initial starting balances at the start of the baseline year (January 2026 or selected startYear)
        var essentialsBalance = 0.00m;
        var growthBalance = 0.00m;
        var stabilityBalance = 0.00m; // Default starting balance set to 0 as requested
        var rewardsBalance = 0.00m;

        var trendPoints = new List<(string month, decimal balance)>();
        
        // Category details for the selected active month
        decimal selectedBudgetEssentials = 0;
        decimal selectedBudgetGrowth = 0;
        decimal selectedBudgetStability = 0;
        decimal selectedBudgetRewards = 0;

        decimal selectedNetEssentials = 0;
        decimal selectedNetGrowth = 0;
        decimal selectedNetStability = 0;
        decimal selectedNetRewards = 0;

        List<Transaction> activeCycleTxs = new();

        decimal selectedRemEssentials = 0;
        decimal selectedRemGrowth = 0;
        decimal selectedRemStability = 0;
        decimal selectedRemRewards = 0;

        string selectedCycleLabel = string.Empty;

        int startYear = Math.Min(2026, activeYear);

        // Roll balances forward chronologically month-by-month and year-by-year cycles
        for (int y = startYear; y <= activeYear; y++)
        {
            for (int m = 1; m <= 12; m++)
            {
                var (cycleStart, cycleEnd, cycleLabel) = GetCycleRange(y, m, cycleDay);

                if (y == activeYear && m == activeMonthIndex)
                {
                    selectedCycleLabel = cycleLabel;
                }

                // 1. Starting Budgets for the cycle
                var mBudgetEssentials = essentialsBalance;
                var mBudgetGrowth = growthBalance;
                var mBudgetStability = stabilityBalance;
                var mBudgetRewards = rewardsBalance;

                if (y == activeYear && m == activeMonthIndex)
                {
                    selectedBudgetEssentials = mBudgetEssentials;
                    selectedBudgetGrowth = mBudgetGrowth;
                    selectedBudgetStability = mBudgetStability;
                    selectedBudgetRewards = mBudgetRewards;
                }

                // 2. Filter transactions strictly within the cycle [cycleStart, cycleEnd]
                var cycleTxs = allTransactions.Where(t =>
                {
                    if (DateTime.TryParse(t.Date, out var date))
                    {
                        return date >= cycleStart && date <= cycleEnd;
                    }
                    return false;
                }).ToList();

                // Calculate category Net Changes in this cycle using LedgerCategory
                var netEssentials = cycleTxs.Sum(t => GetCategoryAmount(t, "Essentials"));
                var netGrowth = cycleTxs.Sum(t => GetCategoryAmount(t, "Growth"));
                var netStability = cycleTxs.Sum(t => GetCategoryAmount(t, "Stability"));
                var netRewards = cycleTxs.Sum(t => GetCategoryAmount(t, "Rewards"));

                if (y == activeYear && m == activeMonthIndex)
                {
                    selectedNetEssentials = netEssentials;
                    selectedNetGrowth = netGrowth;
                    selectedNetStability = netStability;
                    selectedNetRewards = netRewards;
                    activeCycleTxs = cycleTxs;
                }

                // 3. Ending Balances for the cycle
                essentialsBalance = mBudgetEssentials + netEssentials;
                growthBalance = mBudgetGrowth + netGrowth;
                stabilityBalance = mBudgetStability + netStability;
                rewardsBalance = mBudgetRewards + netRewards;

                if (y == activeYear && m == activeMonthIndex)
                {
                    selectedRemEssentials = essentialsBalance;
                    selectedRemGrowth = growthBalance;
                    selectedRemStability = stabilityBalance;
                    selectedRemRewards = rewardsBalance;
                }

                // Record trend point for the active year (Growth category only)
                if (y == activeYear && m <= activeMonthIndex)
                {
                    trendPoints.Add((Months[m - 1], growthBalance));
                }
            }
        }

        // Active cycle range (the transactions for it were already captured during the roll-forward loop above)
        var activeRange = GetCycleRange(year, activeMonthIndex, cycleDay);

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
                date = t.Date,
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
                // result can't flip between requests.
                var paidTx = allTransactions
                    .Where(t =>
                        t.RecurringPaymentId == rp.Id &&
                        DateTime.TryParse(t.Date, out var paidTxDate) &&
                        paidTxDate >= activeRange.start && paidTxDate <= activeRange.end)
                    .OrderBy(t => string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(t => t.Date, StringComparer.Ordinal)
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
                    paidDate = isPaid && !isDiscarded ? paidTx.Date : null
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
            var range = GetCycleRange(activeYear, m, cycleDay);
            var cycleTxsForMonth = allTransactions.Where(t =>
            {
                if (DateTime.TryParse(t.Date, out var date))
                {
                    return date >= range.start && date <= range.end;
                }
                return false;
            });
            yearlyTxs.AddRange(cycleTxsForMonth);
        }

        var yearlyCategoryBreakdown = BuildBreakdown(yearlyTxs);

        // Helper: collect outflow transactions over a span of N cycles ending at the active cycle
        List<Transaction> GetTxsForLastNCycles(int n)
        {
            var result = new List<Transaction>();
            int curMonth = activeMonthIndex;
            int curYear = activeYear;
            for (int i = 0; i < n; i++)
            {
                var range = GetCycleRange(curYear, curMonth, cycleDay);
                result.AddRange(allTransactions.Where(t =>
                {
                    if (DateTime.TryParse(t.Date, out var d)) return d >= range.start && d <= range.end;
                    return false;
                }));
                curMonth--;
                if (curMonth < 1) { curMonth = 12; curYear--; }
            }
            return result;
        }

        var last3CategoryBreakdown = BuildBreakdown(GetTxsForLastNCycles(3));
        var last6CategoryBreakdown = BuildBreakdown(GetTxsForLastNCycles(6));

        // Slice last 3 / last 6 trend points from the accumulated trendPoints list
        var trendPointsList = trendPoints.ToList();
        var last6TrendPoints = trendPointsList.Count >= 6
            ? trendPointsList.GetRange(trendPointsList.Count - 6, 6)
            : trendPointsList.ToList();
        var last3TrendPoints = trendPointsList.Count >= 3
            ? trendPointsList.GetRange(trendPointsList.Count - 3, 3)
            : trendPointsList.ToList();

        var availableYears = allTransactions
            .Select(t => DateTime.TryParse(t.Date, out var d) ? d.Year : (int?)null)
            .Where(y => y.HasValue)
            .Select(y => y!.Value)
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
            var range = GetCycleRange(tempYear, tempMonth, cycleDay);
            var cycleTxsForMonth = allTransactions.Where(t =>
            {
                if (DateTime.TryParse(t.Date, out var date))
                {
                    return date >= range.start && date <= range.end;
                }
                return false;
            }).ToList();

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
        setting.CycleDay = Math.Clamp(updateDto.CycleDay, 1, 31);
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
        if (!string.IsNullOrEmpty(updateDto.StabilityOverflowRedirect))
        {
            setting.StabilityOverflowRedirect = updateDto.StabilityOverflowRedirect;
        }
        setting.Currency = updateDto.Currency;

        await _context.SaveChangesAsync();
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

    private static decimal GetCategoryAmount(Transaction t, string categoryName)
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
