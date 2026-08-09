using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Push;

public partial class PushDispatchService
{
    private async Task<CategoryLimitAlertDispatchSummary> DispatchPendingCategoryAlertsAsync(
        CancellationToken cancellationToken)
    {
        List<string> userIds;
        using (var loadScope = _scopeFactory.CreateScope())
        {
            var context = loadScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var eventUserIds = await context.CategoryLimitAlertEvents
                .IgnoreQueryFilters()
                .Where(item => item.CompletedAt == null)
                .Select(item => item.UserId)
                .ToListAsync(cancellationToken);
            var evaluationUserIds = await context.CategoryLimitAlertEvaluations
                .IgnoreQueryFilters()
                .Select(item => item.UserId)
                .ToListAsync(cancellationToken);
            userIds = eventUserIds.Concat(evaluationUserIds).Distinct(StringComparer.Ordinal).ToList();
        }

        var sent = 0;
        var skipped = 0;
        var disabled = 0;
        foreach (var userId in userIds)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                context.SetCurrentUser(userId);
                var processor = scope.ServiceProvider.GetRequiredService<CategoryLimitAlertProcessor>();
                var summary = await processor.ProcessPendingAsync(cancellationToken);
                sent += summary.Sent;
                skipped += summary.Skipped;
                disabled += summary.Disabled;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped++;
                _logger.LogWarning(ex, "Pending category limit push processing failed for one account.");
            }
        }

        return new CategoryLimitAlertDispatchSummary(sent, skipped, disabled);
    }
}
