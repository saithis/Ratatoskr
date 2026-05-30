using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Internal;

internal partial class EfCoreMetricsBackgroundService<TDbContext>(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    EfCoreMetricsState state,
    EfCoreMetricsSettings<TDbContext> settings,
    ILogger<EfCoreMetricsBackgroundService<TDbContext>> logger
) : BackgroundService
    where TDbContext : DbContext, IInboxDbContext, IOutboxDbContext
{
    private readonly string _contextName = typeof(TDbContext).FullName ?? typeof(TDbContext).Name;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStartingPolling(logger, _contextName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UpdateMetricsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                LogErrorUpdatingMetrics(logger, ex, _contextName);
            }

            await Task.Delay(settings.PollingInterval, timeProvider, stoppingToken);
        }

        LogStoppedPolling(logger, _contextName);
    }

    internal async Task UpdateMetricsAsync(CancellationToken stoppingToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
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
                async (db, ct) =>
                {
                    var n = await db.Set<OutboxMessageEntity>()
                        .CountAsync(x => x.ProcessedAt == null && !x.IsPoisoned, ct);
                    return n;
                },
                stoppingToken
            );

            poisonedOutbox = await CountAsync(
                dbContext,
                async (db, ct) =>
                {
                    var n = await db.Set<OutboxMessageEntity>()
                        .CountAsync(x => x.ProcessedAt == null && x.IsPoisoned, ct);
                    return n;
                },
                stoppingToken
            );
        }

        if (hasInbox)
        {
            pendingInbox = await CountAsync(
                dbContext,
                async (db, ct) =>
                {
                    var n = await db.Set<InboxHandlerStatusEntity>()
                        .CountAsync(x => x.CompletedAt == null && !x.IsPoisoned, ct);
                    return n;
                },
                stoppingToken
            );

            poisonedInbox = await CountAsync(
                dbContext,
                async (db, ct) =>
                {
                    var n = await db.Set<InboxHandlerStatusEntity>()
                        .CountAsync(x => x.CompletedAt == null && x.IsPoisoned, ct);
                    return n;
                },
                stoppingToken
            );
        }

        var snapshot = new DbContextMetrics(
            pendingOutbox,
            poisonedOutbox,
            pendingInbox,
            poisonedInbox
        );
        state.ContextMetrics.AddOrUpdate(
            _contextName,
            static (_, newSnapshot) => newSnapshot,
            static (_, _, newSnapshot) => newSnapshot,
            snapshot
        );
    }

    private async Task<long> CountAsync(
        TDbContext dbContext,
        Func<TDbContext, CancellationToken, Task<long>> count,
        CancellationToken stoppingToken = default
    )
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(settings.QueryTimeout);
        return await count(dbContext, cts.Token);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Starting EF Core Metrics Polling for {DbContext}"
    )]
    private static partial void LogStartingPolling(ILogger logger, string dbContext);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Error updating EF Core metrics for {DbContext}"
    )]
    private static partial void LogErrorUpdatingMetrics(
        ILogger logger,
        Exception ex,
        string dbContext
    );

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Debug,
        Message = "Stopped EF Core Metrics Polling for {DbContext}"
    )]
    private static partial void LogStoppedPolling(ILogger logger, string dbContext);
}
