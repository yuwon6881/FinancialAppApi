using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Services;

public partial class FinancialService
{
    public async Task<string?> UpdateSettingsAsync(
        FinancialSettingsUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (!ObfuscationHelper.TryDeobfuscate(update.TargetStabilityFund, out var targetStabilityFund) || targetStabilityFund < 0m)
        {
            return "Target stability fund is malformed.";
        }
        var allocations = new[] { update.EssentialsAlloc, update.GrowthAlloc, update.StabilityAlloc, update.RewardsAlloc };
        if (allocations.Any(value => value < 0m || value > 1m))
        {
            return "Each income allocation must be between 0% and 100%.";
        }
        if (Math.Abs(allocations.Sum() - 1m) > 0.0001m)
        {
            return "Income allocations must total exactly 100%.";
        }
        if (!CurrencyCatalog.Contains(update.Currency))
        {
            return "Select a supported currency from the list.";
        }

        var setting = await GetOrCreateSettingAsync(cancellationToken);
        var previousStabilityPlan = new FinancialSetting
        {
            TargetStabilityFund = setting.TargetStabilityFund,
            StabilityAlloc = setting.StabilityAlloc,
        };

        var targetStabilityFundChanged = targetStabilityFund != setting.TargetStabilityFund;
        var stabilityAllocChanged = update.StabilityAlloc != setting.StabilityAlloc;
        setting.TargetStabilityFund = targetStabilityFund;
        setting.EssentialsAlloc = update.EssentialsAlloc;
        setting.GrowthAlloc = update.GrowthAlloc;
        setting.StabilityAlloc = update.StabilityAlloc;
        setting.RewardsAlloc = update.RewardsAlloc;
        var newCycleDay = Math.Clamp(update.CycleDay, 1, 31);
        var cycleDayChanged = newCycleDay != setting.CycleDay;
        setting.CycleDay = newCycleDay;
        if (update.DarkMode.HasValue)
        {
            setting.DarkMode = update.DarkMode.Value;
        }
        if (update.HideSensitive.HasValue)
        {
            setting.HideSensitive = update.HideSensitive.Value;
        }
        if (update.StabilityOverflowRedirect != null)
        {
            setting.StabilityOverflowRedirect = update.StabilityOverflowRedirect;
        }
        setting.Currency = update.Currency.Trim().ToUpperInvariant();

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            if (targetStabilityFundChanged || stabilityAllocChanged)
            {
                await _stabilityPlanRevisionService.AppendIfChangedAsync(
                    previousStabilityPlan,
                    setting,
                    DateTime.UtcNow,
                    cancellationToken);
            }
            if (cycleDayChanged)
            {
                await _cycleBalanceService.InvalidateAllAsync(cancellationToken);
            }
            else if (targetStabilityFundChanged || stabilityAllocChanged)
            {
                var currentCycle = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
                    _financialClock.Today,
                    setting.CycleDay);
                await _cycleBalanceService.InvalidateFromAsync(
                    currentCycle.year,
                    currentCycle.monthIndex,
                    cancellationToken);
            }
            await _context.SaveChangesAsync(cancellationToken);
            await dbTransaction.CommitAsync(cancellationToken);
        });
        return null;
    }

    public async Task UpdateDarkModeAsync(bool darkMode, CancellationToken cancellationToken = default)
    {
        var setting = await GetOrCreateSettingAsync(cancellationToken);
        setting.DarkMode = darkMode;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateHideSensitiveAsync(bool hideSensitive, CancellationToken cancellationToken = default)
    {
        var setting = await GetOrCreateSettingAsync(cancellationToken);
        setting.HideSensitive = hideSensitive;
        await _context.SaveChangesAsync(cancellationToken);
    }

    // Records which cycle the user has acknowledged an end-of-cycle summary for. Kept as a
    // tiny dedicated writer (like dark-mode/hide-sensitive) so acknowledging a summary never
    // races or overwrites a full settings edit. A null/blank key clears the marker.
    public async Task UpdateSummarySeenAsync(
        string? cycleKey,
        CancellationToken cancellationToken = default)
    {
        var setting = await GetOrCreateSettingAsync(cancellationToken);
        setting.LastSummaryCycleSeen = string.IsNullOrWhiteSpace(cycleKey) ? null : cycleKey.Trim();
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task SelectPeriodAsync(
        string selectedMonth,
        int selectedYear,
        CancellationToken cancellationToken = default)
    {
        var setting = await GetOrCreateSettingAsync(cancellationToken);
        setting.SelectedMonth = selectedMonth;
        setting.SelectedYear = selectedYear;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<FinancialSetting> GetOrCreateSettingAsync(CancellationToken cancellationToken)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null)
        {
            setting = new FinancialSetting();
            _context.FinancialSettings.Add(setting);
        }
        return setting;
    }
}
