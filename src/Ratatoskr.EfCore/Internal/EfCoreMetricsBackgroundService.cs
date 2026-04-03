using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Internal;

internal class EfCoreMetricsBackgroundService<TDbContext>(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    EfCoreMetricsState state,
    ILogger<EfCoreMetricsBackgroundService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext, IInboxDbContext, IOutboxDbContext
{
    private readonly string _contextName = typeof(TDbContext).Name;

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
            
            // Hardcoded 30 seconds interval for metrics polling
            await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
        }
        
        logger.LogDebug("Stopped EF Core Metrics Polling for {DbContext}", _contextName);
    }

    private async Task UpdateMetricsAsync(CancellationToken stoppingToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        
        // Safety timeout to prevent locking up or waiting too long on DB loads
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        
        dbContext.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        
        var metrics = state.ContextMetrics.GetOrAdd(_contextName, _ => new DbContextMetrics());

        var hasOutbox = scope.ServiceProvider.GetService<OutboxOptionsHolder<TDbContext>>() != null;
        if (hasOutbox)
        {
            metrics.PendingOutboxCount = await dbContext.Set<OutboxMessageEntity>()
                .CountAsync(x => x.ProcessedAt == null && !x.IsPoisoned, cts.Token);
                
            metrics.PoisonedOutboxCount = await dbContext.Set<OutboxMessageEntity>()
                .CountAsync(x => x.ProcessedAt == null && x.IsPoisoned, cts.Token);
        }
        
        var hasInbox = scope.ServiceProvider.GetService<InboxOptionsHolder<TDbContext>>() != null;
        if (hasInbox)
        {
            metrics.PendingInboxCount = await dbContext.Set<InboxHandlerStatusEntity>()
                .CountAsync(x => x.CompletedAt == null && !x.IsPoisoned, cts.Token);
                
            metrics.PoisonedInboxCount = await dbContext.Set<InboxHandlerStatusEntity>()
                .CountAsync(x => x.CompletedAt == null && x.IsPoisoned, cts.Token);
        }
    }
}
