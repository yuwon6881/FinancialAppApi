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

    private static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    public FinancialController(AppDbContext context)
    {
        _context = context;
    }

    public static async Task<(List<object> Pending, List<object> Dismissed)> GetSubscriptionAlertsAsync(AppDbContext context)
    {
        var setting = await context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null) return (new List<object>(), new List<object>());
        var cycleDay = setting.CycleDay;

        var activeRecurring = await context.RecurringPayments.Where(r => r.Active).ToListAsync();
        var today = DateTime.Today;
        var (todayMonth, todayYear) = GetCycleMonthAndYearForDate(today, cycleDay);
        var todayMonthIdx = Array.IndexOf(Months, todayMonth) + 1;

        var pending = new List<object>();
        var dismissed = new List<object>();

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
                    DateTime billingDate;
                    if (cycleDay == 1)
                    {
                        billingDate = new DateTime(cycleStart.Year, cycleStart.Month, Math.Min(rp.DueDate, DateTime.DaysInMonth(cycleStart.Year, cycleStart.Month)));
                    }
                    else
                    {
                        if (rp.DueDate >= cycleStart.Day)
                        {
                            billingDate = new DateTime(cycleStart.Year, cycleStart.Month, Math.Min(rp.DueDate, DateTime.DaysInMonth(cycleStart.Year, cycleStart.Month)));
                        }
                        else
                        {
                            billingDate = new DateTime(cycleEnd.Year, cycleEnd.Month, Math.Min(rp.DueDate, DateTime.DaysInMonth(cycleEnd.Year, cycleEnd.Month)));
                        }
                    }

                    if (billingDate <= today && billingDate >= startDate && (endDate == null || billingDate <= endDate.Value))
                    {
                        var instanceId = $"{rp.Id}-{y}-{m}";
                        var isPaid = await context.Transactions.AnyAsync(t => t.Id == instanceId);
                        if (!isPaid)
                        {
                            var item = new
                            {
                                id = instanceId,
                                recurringPaymentId = rp.Id,
                                name = rp.Name,
                                amount = rp.Amount,
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

        return (pending, dismissed);
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

    public static IReadOnlyList<(string LedgerCategory, decimal Amount)> GetIncomeSplitAllocations(decimal amount, FinancialSetting setting)
    {
        var growthAmount = decimal.Round(amount * setting.GrowthAlloc, 2, MidpointRounding.AwayFromZero);
        var stabilityAmount = decimal.Round(amount * setting.StabilityAlloc, 2, MidpointRounding.AwayFromZero);
        var rewardsAmount = decimal.Round(amount * setting.RewardsAlloc, 2, MidpointRounding.AwayFromZero);
        var essentialsAmount = amount - growthAmount - stabilityAmount - rewardsAmount;

        return new List<(string LedgerCategory, decimal Amount)>
        {
            ("Essentials", essentialsAmount),
            ("Growth", growthAmount),
            ("Stability", stabilityAmount),
            ("Rewards", rewardsAmount)
        };
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
                MonthlyIncome = 4000.00m,
                TargetStabilityFund = 10000.00m,
                SelectedMonth = now.ToString("MMM"),
                SelectedYear = now.Year,
                EssentialsAlloc = 0.50m,
                GrowthAlloc = 0.25m,
                StabilityAlloc = 0.15m,
                RewardsAlloc = 0.10m,
                CycleDay = 28
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

        var trendPoints = new List<object>();
        
        // Category details for the selected active month
        decimal selectedBudgetEssentials = 0;
        decimal selectedBudgetGrowth = 0;
        decimal selectedBudgetStability = 0;
        decimal selectedBudgetRewards = 0;

        decimal selectedNetEssentials = 0;
        decimal selectedNetGrowth = 0;
        decimal selectedNetStability = 0;
        decimal selectedNetRewards = 0;

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
                var netEssentials = cycleTxs.Where(t => string.Equals(t.LedgerCategory, "Essentials", StringComparison.OrdinalIgnoreCase)).Sum(t => t.Amount);
                var netGrowth = cycleTxs.Where(t => string.Equals(t.LedgerCategory, "Growth", StringComparison.OrdinalIgnoreCase)).Sum(t => t.Amount);
                var netStability = cycleTxs.Where(t => string.Equals(t.LedgerCategory, "Stability", StringComparison.OrdinalIgnoreCase)).Sum(t => t.Amount);
                var netRewards = cycleTxs.Where(t => string.Equals(t.LedgerCategory, "Rewards", StringComparison.OrdinalIgnoreCase)).Sum(t => t.Amount);

                if (y == activeYear && m == activeMonthIndex)
                {
                    selectedNetEssentials = netEssentials;
                    selectedNetGrowth = netGrowth;
                    selectedNetStability = netStability;
                    selectedNetRewards = netRewards;
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
                    trendPoints.Add(new
                    {
                        month = Months[m - 1],
                        balance = growthBalance
                    });
                }
            }
        }

        // Target allocations and budgets (using setting values)
        var targetEssentials = setting.MonthlyIncome * setting.EssentialsAlloc;
        var targetGrowth = setting.MonthlyIncome * setting.GrowthAlloc;
        var targetStability = setting.MonthlyIncome * setting.StabilityAlloc;
        var targetRewards = setting.MonthlyIncome * setting.RewardsAlloc;

        var categories = new[]
        {
            new { name = "Essentials", allocation = setting.EssentialsAlloc, target = targetEssentials, budget = selectedBudgetEssentials, netChange = selectedNetEssentials, remaining = selectedRemEssentials },
            new { name = "Growth", allocation = setting.GrowthAlloc, target = targetGrowth, budget = selectedBudgetGrowth, netChange = selectedNetGrowth, remaining = selectedRemGrowth },
            new { name = "Stability", allocation = setting.StabilityAlloc, target = targetStability, budget = selectedBudgetStability, netChange = selectedRemStability, remaining = selectedRemStability },
            new { name = "Rewards", allocation = setting.RewardsAlloc, target = targetRewards, budget = selectedBudgetRewards, netChange = selectedNetRewards, remaining = selectedRemRewards }
        };

        // Monthly Stats calculations
        var totalBalance = selectedRemEssentials + selectedRemStability + selectedRemRewards;

        // Active cycle range and transactions
        var activeRange = GetCycleRange(year, activeMonthIndex, cycleDay);
        var activeCycleTxs = allTransactions.Where(t =>
        {
            if (DateTime.TryParse(t.Date, out var date))
            {
                return date >= activeRange.start && date <= activeRange.end;
            }
            return false;
        }).ToList();

        var monthlyInflow = activeCycleTxs.Where(t => t.Amount > 0).Sum(t => t.Amount);

        // Outflow (Absolute sum of negative transactions in selected cycle)
        var monthlyOutflow = Math.Abs(activeCycleTxs.Where(t => t.Amount < 0).Sum(t => t.Amount));

        var activeRecurringTotal = Math.Abs(allRecurring.Where(r => r.Active).Sum(r => r.Amount));

        // Metric ratios from Excel formulas
        var growthPercentAchieved = selectedNetGrowth / (targetGrowth > 0 ? targetGrowth : 1m);
        
        // Essentials remaining: starts at 100% (= targetEssentials) and decreases as spend is logged.
        // netEssentials is negative for outflows, so (targetEssentials + netEssentials) = remaining.
        var essentialsPercentRemaining = targetEssentials > 0 
            ? Math.Max(0m, (targetEssentials + selectedNetEssentials) / targetEssentials) 
            : 0m;
            
        var stabilityPercentReached = selectedRemStability / (setting.TargetStabilityFund > 0 ? setting.TargetStabilityFund : 1m);

        // Fetch recent manual transactions for the cycle (max 5)
        var recentTransactions = activeCycleTxs
            .Where(t => !t.Id.StartsWith("rec-"))
            .OrderByDescending(t => t.Date)
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

        var selectedMonthRecurring = activeCycleTxs
            .Where(t => t.Id.StartsWith("rec-"))
            .Select(t => new
            {
                id = t.Id,
                name = t.Description,
                amount = Math.Abs(t.Amount), // positive on UI
                category = t.Category,
                ledgerCategory = t.LedgerCategory,
                dueDate = t.Date
            })
            .ToList();

        var (pendingNotifications, dismissedNotifications) = await GetSubscriptionAlertsAsync(_context);

        var monthlyCategoryBreakdown = activeCycleTxs
            .Where(t => t.Amount < 0)
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "Other" : t.Category)
            .Select(g => new
            {
                category = g.Key,
                amount = Math.Abs(g.Sum(t => t.Amount))
            })
            .OrderByDescending(b => b.amount)
            .ToList();

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

        var yearlyCategoryBreakdown = yearlyTxs
            .Where(t => t.Amount < 0)
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "Other" : t.Category)
            .Select(g => new
            {
                category = g.Key,
                amount = Math.Abs(g.Sum(t => t.Amount))
            })
            .OrderByDescending(b => b.amount)
            .ToList();

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

        List<object> BuildBreakdown(List<Transaction> txs) => txs
            .Where(t => t.Amount < 0)
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "Other" : t.Category)
            .Select(g => (object)new { category = g.Key, amount = Math.Abs(g.Sum(t => t.Amount)) })
            .OrderByDescending(b => ((dynamic)b).amount)
            .ToList();

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

        return Ok(new
        {
            setting = new
            {
                setting.MonthlyIncome,
                setting.TargetStabilityFund,
                setting.SelectedMonth,
                setting.SelectedYear,
                setting.EssentialsAlloc,
                setting.GrowthAlloc,
                setting.StabilityAlloc,
                setting.RewardsAlloc,
                setting.CycleDay
            },
            cycleLabel = selectedCycleLabel,
            categories,
            stats = new
            {
                totalBalance,
                monthlyIncome = setting.MonthlyIncome,
                monthlyInflow,
                monthlyExpenses = monthlyOutflow,
                activeRecurringTotal,
                growthPercentAchieved = (double)Math.Max(0, growthPercentAchieved),
                essentialsPercentRemaining = (double)essentialsPercentRemaining,
                stabilityPercentReached = (double)Math.Max(0, stabilityPercentReached)
            },
            recentTransactions,
            activeRecurringPayments = selectedMonthRecurring,
            trendPoints,
            last3TrendPoints,
            last6TrendPoints,
            pendingNotifications,
            dismissedNotifications,
            monthlyCategoryBreakdown,
            last3CategoryBreakdown,
            last6CategoryBreakdown,
            yearlyCategoryBreakdown
        });
    }

    // PUT: api/financial/settings
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] FinancialSetting updateDto)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null)
        {
            setting = new FinancialSetting();
            _context.FinancialSettings.Add(setting);
        }

        setting.MonthlyIncome = updateDto.MonthlyIncome;
        setting.TargetStabilityFund = updateDto.TargetStabilityFund;
        setting.EssentialsAlloc = updateDto.EssentialsAlloc;
        setting.GrowthAlloc = updateDto.GrowthAlloc;
        setting.StabilityAlloc = updateDto.StabilityAlloc;
        setting.RewardsAlloc = updateDto.RewardsAlloc;
        setting.CycleDay = updateDto.CycleDay;

        await _context.SaveChangesAsync();
        return NoContent();
    }

    // POST: api/financial/select-period
    [HttpPost("select-period")]
    public async Task<IActionResult> SelectPeriod([FromBody] FinancialSetting periodDto)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null)
        {
            setting = new FinancialSetting();
            _context.FinancialSettings.Add(setting);
        }

        setting.SelectedMonth = periodDto.SelectedMonth;
        setting.SelectedYear = periodDto.SelectedYear;

        await _context.SaveChangesAsync();
        return NoContent();
    }
}
