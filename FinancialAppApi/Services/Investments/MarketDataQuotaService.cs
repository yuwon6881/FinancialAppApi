using System.Data;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

public sealed class MarketDataQuotaService(AppDbContext context, IMarketDataProvider provider)
{
    private string ProviderId => provider.Descriptor.Id;
    private MarketDataQuotaPolicy Policy => provider.Descriptor.QuotaPolicy;

    public async Task<int> ReserveRefreshAsync(int requested, CancellationToken cancellationToken)
    {
        if (requested <= 0) return 0;
        var result = 0;
        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var minuteStart = MinuteStart(now);
            var dayStart = now.Date;
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireLockAsync(cancellationToken);
            await PruneExpiredAsync(now, cancellationToken);
            var minute = await GetOrCreateAsync(Scope("refresh-minute"), minuteStart, cancellationToken);
            var providerMinute = await GetOrCreateAsync(Scope("provider-minute"), minuteStart, cancellationToken);
            var day = await GetOrCreateAsync(Scope("provider-day"), dayStart, cancellationToken);
            var userDay = await GetOrCreateAsync(UserDayScope(), dayStart, cancellationToken);
            var allowed = Math.Min(requested, Math.Min(
                Math.Max(0, Policy.RefreshCallsPerMinute - minute.Used),
                Math.Min(
                    Math.Max(0, Policy.RefreshCallsPerMinute + Policy.DiscoveryCallsPerMinute - providerMinute.Used),
                    Math.Min(
                        Math.Max(0, Policy.DailyCallCeiling - day.Used),
                        Math.Max(0, Policy.PerUserDailyCallCeiling - userDay.Used)))));
            if (allowed > 0)
            {
                Increment(minute, allowed, now);
                Increment(providerMinute, allowed, now);
                Increment(day, allowed, now);
                Increment(userDay, allowed, now);
                await context.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            result = allowed;
        });
        return result;
    }

    public async Task<bool> ReserveDiscoveryAsync(CancellationToken cancellationToken)
    {
        var result = false;
        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var minuteStart = MinuteStart(now);
            var dayStart = now.Date;
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireLockAsync(cancellationToken);
            await PruneExpiredAsync(now, cancellationToken);
            var providerMinute = await GetOrCreateAsync(Scope("provider-minute"), minuteStart, cancellationToken);
            var discoveryMinute = await GetOrCreateAsync(Scope("discovery-minute"), minuteStart, cancellationToken);
            var day = await GetOrCreateAsync(Scope("provider-day"), dayStart, cancellationToken);
            var userDay = await GetOrCreateAsync(UserDayScope(), dayStart, cancellationToken);
            var allowed = discoveryMinute.Used < Policy.DiscoveryCallsPerMinute &&
                          providerMinute.Used < Policy.RefreshCallsPerMinute + Policy.DiscoveryCallsPerMinute &&
                          day.Used < Policy.DailyCallCeiling &&
                          userDay.Used < Policy.PerUserDailyCallCeiling;
            if (allowed)
            {
                Increment(discoveryMinute, 1, now);
                Increment(providerMinute, 1, now);
                Increment(day, 1, now);
                Increment(userDay, 1, now);
                await context.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            result = allowed;
        });
        return result;
    }

    public async Task<int> ReserveOperatorAsync(int requested, CancellationToken cancellationToken)
    {
        if (requested <= 0) return 0;
        var result = 0;
        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var minuteStart = MinuteStart(now);
            var dayStart = now.Date;
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireLockAsync(cancellationToken);
            await PruneExpiredAsync(now, cancellationToken);
            var providerMinute = await GetOrCreateAsync(Scope("provider-minute"), minuteStart, cancellationToken);
            var operatorMinute = await GetOrCreateAsync(Scope("operator-minute"), minuteStart, cancellationToken);
            var day = await GetOrCreateAsync(Scope("provider-day"), dayStart, cancellationToken);
            var allowed = Math.Min(requested, Math.Min(
                Math.Max(0, Policy.RefreshCallsPerMinute - operatorMinute.Used),
                Math.Min(
                    Math.Max(0, Policy.RefreshCallsPerMinute + Policy.DiscoveryCallsPerMinute - providerMinute.Used),
                    Math.Max(0, Policy.DailyCallCeiling - day.Used))));
            if (allowed > 0)
            {
                Increment(operatorMinute, allowed, now);
                Increment(providerMinute, allowed, now);
                Increment(day, allowed, now);
                await context.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            result = allowed;
        });
        return result;
    }

    private Task AcquireLockAsync(CancellationToken cancellationToken)
        => context.Database.IsRelational()
            ? context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(741923601)", cancellationToken)
            : Task.CompletedTask;

    private async Task PruneExpiredAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-1).AddHours(-1);
        var expired = await context.MarketDataQuotaWindows.Where(value => value.WindowStart < cutoff)
            .Take(500).ToListAsync(cancellationToken);
        if (expired.Count > 0) context.MarketDataQuotaWindows.RemoveRange(expired);
    }

    private async Task<MarketDataQuotaWindow> GetOrCreateAsync(
        string scope, DateTime start, CancellationToken cancellationToken)
    {
        var window = await context.MarketDataQuotaWindows.FirstOrDefaultAsync(
            value => value.Scope == scope && value.WindowStart == start, cancellationToken);
        if (window is not null) return window;
        window = new MarketDataQuotaWindow { Scope = scope, WindowStart = start };
        context.MarketDataQuotaWindows.Add(window);
        return window;
    }

    private static DateTime MinuteStart(DateTime now)
        => new(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);

    private static void Increment(MarketDataQuotaWindow window, int amount, DateTime now)
    {
        window.Used += amount;
        window.UpdatedAt = now;
    }

    private string Scope(string value) => $"{ProviderId}:{value}";
    private string UserDayScope() => Scope($"user-day:{context.RequireCurrentUserId()}");
}
