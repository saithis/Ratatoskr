using System.Threading.Channels;
using Medallion.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Base class for background services that poll for work with a trigger channel
/// and distributed lock acquisition. Provides crash restart, trigger/polling,
/// and lock-based concurrency control.
/// </summary>
internal abstract class PollingBackgroundService(
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    ILogger logger) : BackgroundService
{
    private readonly Channel<byte> _triggerChannel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    protected abstract string ProcessorName { get; }
    protected abstract TimeSpan PollingInterval { get; }
    protected abstract TimeSpan RestartDelay { get; }
    protected abstract TimeSpan LockAcquireTimeout { get; }
    protected abstract string LockName { get; }

    /// <summary>
    /// Signals the processor to check for pending work immediately.
    /// Non-blocking: if a trigger is already pending it is dropped.
    /// </summary>
    public ValueTask TriggerAsync(CancellationToken cancellationToken = default)
    {
        _triggerChannel.Writer.TryWrite(1);
        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting {Processor}", ProcessorName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessWithLockAsync(stoppingToken);
                await WaitForTriggerOrTimeoutAsync(stoppingToken);
            }
            catch (Exception e)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;
                logger.LogCritical(e, "{Processor} crashed, trying to restart in {Delay}", ProcessorName, RestartDelay);
                await Task.Delay(RestartDelay, timeProvider, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                    break;
            }
        }

        logger.LogInformation("Stopped {Processor}", ProcessorName);
    }

    private async Task ProcessWithLockAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Trying to acquire distributed lock '{LockName}'", LockName);
        await using IDistributedSynchronizationHandle? dLock =
            await distributedLockProvider.TryAcquireLockAsync(
                LockName,
                LockAcquireTimeout,
                stoppingToken);

        if (dLock == null)
        {
            logger.LogInformation("Failed to acquire lock for {Processor}, processing will be skipped", ProcessorName);
            return;
        }

        logger.LogDebug("{Processor} distributed lock acquired", ProcessorName);

        // Combine the host stopping token with the lock's HandleLostToken so that
        // processing stops immediately if the lock is lost (e.g. network partition).
        using var linkedCts = dLock.HandleLostToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, dLock.HandleLostToken)
            : CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var processingToken = linkedCts.Token;

        try
        {
            await ProcessBatchesAsync(processingToken);
        }
        catch (OperationCanceledException) when (dLock.HandleLostToken.IsCancellationRequested)
        {
            logger.LogWarning("Distributed lock was lost during {Processor} processing", ProcessorName);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while processing {Processor} messages", ProcessorName);
        }
    }

    /// <summary>
    /// Processes batches of work until no more items are found.
    /// Implementations should create a new DI scope and DbContext per batch.
    /// Return 0 from each batch call when there is no more work to do.
    /// </summary>
    protected abstract Task ProcessBatchesAsync(CancellationToken cancellationToken);

    private async Task WaitForTriggerOrTimeoutAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Waiting for {Processor} trigger or {Delay} timeout", ProcessorName, PollingInterval);

        var channelTask = _triggerChannel.Reader.WaitToReadAsync(stoppingToken).AsTask();
        var delayTask = Task.Delay(PollingInterval, timeProvider, stoppingToken);

        var completedTask = await Task.WhenAny(channelTask, delayTask);

        if (completedTask == channelTask && channelTask.IsCompletedSuccessfully && channelTask.Result)
        {
            _triggerChannel.Reader.TryRead(out _);
            logger.LogDebug("{Processor} triggered immediately via channel", ProcessorName);
        }
        else
        {
            logger.LogDebug("{Processor} polling interval elapsed", ProcessorName);
        }
    }
}
