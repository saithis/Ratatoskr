using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Internal;

internal class EfCoreMetricsBackgroundService<TDbContext>(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    EfCoreMetricsState state,
    EfCoreMetricsSettings<TDbContext> settings,
    ILogger<EfCoreMetricsBackgroundService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext, IInboxDbContext, IOutboxDbContext
{
    private readonly string _contextName = typeof(TDbContext).FullName ?? typeof(TDbContext).Name;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Starting EF Core Metrics Polling for {DbContext}", _contextName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UpdateMetricsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                if (stoppingToken.IsCancellationRequested) break;
                logger.LogError(ex, "Error updating EF Core metrics for {DbContext}", _contextName);
            }

            await Task.Delay(settings.PollingInterval, timeProvider, stoppingToken);
        }

        logger.LogDebug("Stopped EF Core Metrics Polling for {DbContext}", _contextName);
    }

    internal async Task UpdateMetricsAsync(CancellationToken stoppingToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

        dbContext.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var hasOutbox = scope.ServiceProvider.GetService<OutboxOptionsHolder<TDbContext>>() != null;
        var hasInbox = scope.ServiceProvider.GetService<InboxOptionsHolder<TDbContext>>() != null;

        long pendingOutbox = 0;
        long poisonedOutbox = 0;
        long pendingInbox = 0;
        long poisonedInbox = 0;

        if (hasOutbox)
        {
            pendingOutbox = await CountAsync(
                dbContext,
                stoppingToken,
                async (db, ct) =>
                {
                    var n = await db.Set<OutboxMessageEntity>()
                        .CountAsync(x => x.ProcessedAt == null && !x.IsPoisoned, ct);
                    return (long)n;
                });

            poisonedOutbox = await CountAsync(
                dbContext,
                stoppingToken,
                async (db, ct) =>
                {
                    var n = await db.Set<OutboxMessageEntity>()
                        .CountAsync(x => x.ProcessedAt == null && x.IsPoisoned, ct);
                    return (long)n;
                });
        }

        if (hasInbox)
        {
            pendingInbox = await CountAsync(
                dbContext,
                stoppingToken,
                async (db, ct) =>
                {
                    var n = await db.Set<InboxHandlerStatusEntity>()
                        .CountAsync(x => x.CompletedAt == null && !x.IsPoisoned, ct);
                    return (long)n;
                });

            poisonedInbox = await CountAsync(
                dbContext,
                stoppingToken,
                async (db, ct) =>
                {
                    var n = await db.Set<InboxHandlerStatusEntity>()
                        .CountAsync(x => x.CompletedAt == null && x.IsPoisoned, ct);
                    return (long)n;
                });
        }

        var snapshot = new DbContextMetrics(pendingOutbox, poisonedOutbox, pendingInbox, poisonedInbox);
        state.ContextMetrics.AddOrUpdate(_contextName, snapshot, (_, _) => snapshot);
    }

    private async Task<long> CountAsync(
        TDbContext dbContext,
        CancellationToken stoppingToken,
        Func<TDbContext, CancellationToken, Task<long>> count)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(settings.QueryTimeout);
        return await count(dbContext, cts.Token);
    }
}
