using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

internal static class CategorySpendingGuideWriter
{
    public static async Task UpsertAsync(
        AppDbContext context, FinancialClock clock, string categoryName, decimal? limitAmount)
    {
        var cycleDay = await context.FinancialSettings.AsNoTracking()
            .Select(setting => (int?)setting.CycleDay).FirstOrDefaultAsync()
            ?? FinancialConstants.DefaultCycleDay;
        var (year, month) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(clock.Today, cycleDay);
        var cycleKey = $"{year:D4}-{month:D2}";
        var guide = context.CategorySpendingGuides.Local.FirstOrDefault(item =>
                item.CategoryName == categoryName && item.EffectiveFromCycleKey == cycleKey)
            ?? await context.CategorySpendingGuides.FirstOrDefaultAsync(item =>
                item.CategoryName == categoryName && item.EffectiveFromCycleKey == cycleKey);
        if (guide != null)
        {
            guide.LimitAmount = limitAmount;
            return;
        }

        context.CategorySpendingGuides.Add(new CategorySpendingGuide
        {
            Id = $"guide-{Guid.NewGuid():N}",
            CategoryName = categoryName,
            EffectiveFromCycleKey = cycleKey,
            LimitAmount = limitAmount,
        });
    }
}
