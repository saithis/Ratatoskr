using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class OutboxCleanupService<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    OutboxOptionsHolder<TDbContext> optionsHolder,
    TimeProvider timeProvider,
    ILogger<OutboxCleanupService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = optionsHolder.Options;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting OutboxCleanupService");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_options.CleanupInterval, timeProvider, stoppingToken);

            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "OutboxCleanupService encountered an error during cleanup");
            }
        }

        logger.LogInformation("Stopped OutboxCleanupService");
    }

    internal async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var cutoff = timeProvider.GetUtcNow() - _options.RetentionPeriod!.Value;
        var totalDeleted = 0;
        int deleted;

        do
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

            deleted = await dbContext.Set<OutboxMessageEntity>()
                .Where(x => x.ProcessedAt != null
                          && !x.IsPoisoned
                          && x.ProcessedAt < cutoff)
                .OrderBy(x => x.ProcessedAt)
                .Take(_options.CleanupBatchSize)
                .ExecuteDeleteAsync(cancellationToken);

            totalDeleted += deleted;
        } while (deleted == _options.CleanupBatchSize);

        RatatoskrDiagnostics.OutboxCleanupDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
        if (totalDeleted > 0)
        {
            RatatoskrDiagnostics.OutboxCleanupCount.Add(totalDeleted);
            logger.LogInformation("OutboxCleanupService deleted {Count} processed message(s)", totalDeleted);
        }

        return totalDeleted;
    }
}
