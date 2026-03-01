using System.Threading.Channels;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class InboxProcessor<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    IOptions<InboxOptions> options,
    ILogger<InboxProcessor<TDbContext>> logger)
    : BackgroundService where TDbContext : DbContext, IInboxDbContext
{
    private readonly InboxOptions _options = options.Value;

    // Channel for immediate triggering — same pattern as OutboxProcessor
    private readonly Channel<byte> _triggerChannel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// Signals the processor to check for pending handler deliveries immediately.
    /// Non-blocking: if a trigger is already pending it is dropped.
    /// </summary>
    public ValueTask TriggerAsync(CancellationToken cancellationToken = default)
    {
        _triggerChannel.Writer.TryWrite(1);
        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting InboxProcessor with options: {@Options}", _options);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessInboxAsync(stoppingToken);
                await WaitForTriggerOrTimeoutAsync(stoppingToken);
            }
            catch (Exception e)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;
                logger.LogCritical(e, "InboxProcessor crashed, trying to restart in {Delay}", _options.RestartDelay);
                await Task.Delay(_options.RestartDelay, timeProvider, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                    break;
            }
        }

        logger.LogInformation("Stopped InboxProcessor");
    }

    private async Task ProcessInboxAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Trying to acquire distributed lock '{LockName}'", _options.LockName);
        await using IDistributedSynchronizationHandle? dLock =
            await distributedLockProvider.TryAcquireLockAsync(
                _options.LockName,
                _options.LockAcquireTimeout,
                stoppingToken);

        if (dLock == null)
        {
            logger.LogInformation("Failed to acquire inbox lock, processing will be skipped");
            return;
        }

        logger.LogDebug("Inbox distributed lock acquired");

        // Combine the host stopping token with the lock's HandleLostToken so that
        // processing stops immediately if the lock is lost (e.g. network partition).
        using var linkedCts = dLock.HandleLostToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, dLock.HandleLostToken)
            : CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var processingToken = linkedCts.Token;

        try
        {
            using IServiceScope serviceScope = serviceScopeFactory.CreateScope();
            var sp = serviceScope.ServiceProvider;

            var dbContext = sp.GetRequiredService<TDbContext>();
            var handlerRegistry = sp.GetRequiredService<InboxHandlerRegistry>();
            var observers = sp.GetServices<IMessageActivityObserver>();
            var messageSerializer = sp.GetRequiredService<IMessageSerializer>();

            var processor = new InboxMessageProcessor<TDbContext>(
                dbContext, serviceScopeFactory, handlerRegistry, timeProvider,
                _options, observers, messageSerializer, logger);

            while (true)
            {
                logger.LogDebug("Checking inbox for pending handler deliveries");
                var processed = await processor.ProcessBatchAsync(
                    includeStuckMessageDetection: true,
                    processingToken);
                if (processed == 0)
                    return;
            }
        }
        catch (OperationCanceledException) when (dLock.HandleLostToken.IsCancellationRequested)
        {
            logger.LogWarning("Distributed lock was lost during inbox processing");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while processing inbox messages");
        }
    }

    private async Task WaitForTriggerOrTimeoutAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Waiting for inbox trigger or {Delay} timeout", _options.PollingInterval);

        var channelTask = _triggerChannel.Reader.WaitToReadAsync(stoppingToken).AsTask();
        var delayTask = Task.Delay(_options.PollingInterval, timeProvider, stoppingToken);

        var completedTask = await Task.WhenAny(channelTask, delayTask);

        if (completedTask == channelTask && channelTask.IsCompletedSuccessfully && channelTask.Result)
        {
            _triggerChannel.Reader.TryRead(out _);
            logger.LogDebug("Inbox triggered immediately via channel");
        }
        else
        {
            logger.LogDebug("Inbox polling interval elapsed");
        }
    }
}
