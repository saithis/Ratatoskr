using System.Threading.Channels;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class OutboxProcessor<TDbContext>(
    IServiceScopeFactory serviceScopeFactory, 
    IDistributedLockProvider distributedLockProvider, 
    TimeProvider timeProvider,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor<TDbContext>> logger) 
    : BackgroundService where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = options.Value;
    
    // Channel for immediate triggering - bounded with capacity 1, dropping oldest if full
    // This provides backpressure and prevents unbounded memory growth
    private readonly Channel<byte> _triggerChannel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    
    /// <summary>
    /// Signals the processor to check for messages immediately.
    /// This is a fast, non-blocking operation that writes to an in-memory channel.
    /// </summary>
    public ValueTask TriggerAsync(CancellationToken cancellationToken = default)
    {
        // TryWrite is non-blocking; if channel is full, the signal is dropped
        // which is fine because there's already a pending trigger
        _triggerChannel.Writer.TryWrite(1);
        return ValueTask.CompletedTask;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting OutboxProcessor with options: {@Options}", _options);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOutboxAsync(stoppingToken);
                await WaitForTriggerOrTimeoutAsync(stoppingToken);
            }
            catch (Exception e)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;
                logger.LogCritical(e, "OutboxProcessor crashed, trying to restart in {Delay}", 
                    _options.RestartDelay);
                await Task.Delay(_options.RestartDelay, timeProvider, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                    break;
            }
        }

        logger.LogInformation("Stopped OutboxProcessor");
    }

    private async Task ProcessOutboxAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Trying to acquire distributed lock '{LockName}'", _options.LockName);
        await using IDistributedSynchronizationHandle? dLock =
            await distributedLockProvider.TryAcquireLockAsync(
                _options.LockName, 
                _options.LockAcquireTimeout,
                stoppingToken);

        if (dLock == null)
        {
            logger.LogInformation("Failed to acquire lock, outbox processing will be skipped");
            return;
        }

        logger.LogDebug("Distributed lock acquired");

        // Combine the host stopping token with the lock's HandleLostToken so that
        // processing stops immediately if the lock is lost (e.g. network partition).
        using var linkedCts = dLock.HandleLostToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, dLock.HandleLostToken)
            : CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var processingToken = linkedCts.Token;

        try
        {
            using IServiceScope serviceScope = serviceScopeFactory.CreateScope();

            var dbContext = serviceScope.ServiceProvider.GetRequiredService<TDbContext>();
            var messageSenders = serviceScope.ServiceProvider.GetServices<IMessageSender>();
            var activityObservers = serviceScope.ServiceProvider.GetServices<IMessageActivityObserver>();

            // Use shared processor logic - ensures tests and production use SAME code
            var processor = new OutboxMessageProcessor<TDbContext>(
                dbContext, messageSenders, timeProvider, _options, activityObservers, logger);

            while (true)
            {
                logger.LogDebug("Checking outbox for unsent messages");

                // Process one batch with stuck message detection enabled (for production)
                var processedCount = await processor.ProcessBatchAsync(
                    includeStuckMessageDetection: true,
                    processingToken);

                if (processedCount == 0)
                    return;
            }
        }
        catch (OperationCanceledException) when (dLock.HandleLostToken.IsCancellationRequested)
        {
            logger.LogWarning("Distributed lock was lost during outbox processing");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while processing outbox messages");
        }
    }

    /// <summary>
    /// Waits for either a trigger signal from the channel or the polling interval timeout.
    /// This provides immediate response when messages are added while still having
    /// fallback polling for crash recovery scenarios.
    /// </summary>
    private async Task WaitForTriggerOrTimeoutAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Waiting for trigger or {Delay} timeout", _options.PollingInterval);

        var channelTask = _triggerChannel.Reader.WaitToReadAsync(stoppingToken).AsTask();
        var delayTask = Task.Delay(_options.PollingInterval, timeProvider, stoppingToken);

        var completedTask = await Task.WhenAny(channelTask, delayTask);

        if (completedTask == channelTask && channelTask.IsCompletedSuccessfully && channelTask.Result)
        {
            _triggerChannel.Reader.TryRead(out _);
            logger.LogDebug("Triggered immediately via channel");
        }
        else
        {
            logger.LogDebug("Polling interval elapsed, checking for messages");
        }
    }
}