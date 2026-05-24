using System.Diagnostics;
using System.Threading.Channels;
using Medallion.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Base class for background services that poll for work with a trigger channel
/// and distributed lock acquisition. Provides crash restart, trigger/polling,
/// and lock-based concurrency control.
/// </summary>
internal abstract partial class PollingBackgroundService(
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    ILogger logger
) : BackgroundService
{
    public DateTimeOffset LastSuccessfulProcessingAt { get; private set; } =
        timeProvider.GetUtcNow();

    private readonly Channel<byte> _triggerChannel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        }
    );

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
        LogStartingProcessor(logger, ProcessorName);

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
                {
                    break;
                }

                LogProcessorCrashed(logger, e, ProcessorName, RestartDelay);
                await Task.Delay(RestartDelay, timeProvider, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        LogStoppedProcessor(logger, ProcessorName);
    }

    private async Task ProcessWithLockAsync(CancellationToken stoppingToken)
    {
        LogTryingToAcquireDistributedLock(logger, LockName);
        await using var dLock = await distributedLockProvider.TryAcquireLockAsync(
            LockName,
            LockAcquireTimeout,
            stoppingToken
        );

        if (dLock == null)
        {
            LogFailedToAcquireLock(logger, ProcessorName);
            RatatoskrDiagnostics.LockAcquisitionFailure.Add(
                1,
                new TagList { { "processor", ProcessorName } }
            );

            // Being a passive node is a valid state, so it's considered successfully healthy
            LastSuccessfulProcessingAt = timeProvider.GetUtcNow();
            return;
        }

        LogDistributedLockAcquired(logger, ProcessorName);

        // Combine the host stopping token with the lock's HandleLostToken so that
        // processing stops immediately if the lock is lost (e.g. network partition).
        using var linkedCts = dLock.HandleLostToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, dLock.HandleLostToken)
            : CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var processingToken = linkedCts.Token;

        try
        {
            await ProcessBatchesAsync(processingToken);
            LastSuccessfulProcessingAt = timeProvider.GetUtcNow();
        }
        // processingToken links stoppingToken + HandleLostToken. If work is canceled while the host is
        // still running, the only source is HandleLostToken (lock loss). Do not rely on
        // HandleLostToken.IsCancellationRequested in the filter — it can disagree with the token on
        // OperationCanceledException/TaskCanceledException from Task.Delay in some runtimes.
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogStoppingSignalReceived(logger, ProcessorName);
        }
        catch (OperationCanceledException) when (dLock.HandleLostToken.CanBeCanceled)
        {
            LogDistributedLockWasLost(logger, ProcessorName);
            RatatoskrDiagnostics.LockLost.Add(1, new TagList { { "processor", ProcessorName } });
        }
        catch (Exception e)
        {
            LogErrorWhileProcessingMessages(logger, e, ProcessorName);
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
        LogWaitingForTriggerOrTimeout(logger, ProcessorName, PollingInterval);

        var channelTask = _triggerChannel.Reader.WaitToReadAsync(stoppingToken).AsTask();
        var delayTask = Task.Delay(PollingInterval, timeProvider, stoppingToken);

        var completedTask = await Task.WhenAny(channelTask, delayTask);

        if (
            completedTask == channelTask
            && channelTask.IsCompletedSuccessfully
            && await channelTask
        )
        {
            _triggerChannel.Reader.TryRead(out _);
            LogTriggeredImmediatelyViaChannel(logger, ProcessorName);
        }
        else
        {
            LogPollingIntervalElapsed(logger, ProcessorName);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting {Processor}")]
    private static partial void LogStartingProcessor(ILogger logger, string processor);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Critical,
        Message = "{Processor} crashed, trying to restart in {Delay}"
    )]
    private static partial void LogProcessorCrashed(
        ILogger logger,
        Exception ex,
        string processor,
        TimeSpan delay
    );

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Stopped {Processor}")]
    private static partial void LogStoppedProcessor(ILogger logger, string processor);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "Trying to acquire distributed lock '{LockName}'"
    )]
    private static partial void LogTryingToAcquireDistributedLock(ILogger logger, string lockName);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Failed to acquire lock for {Processor}, processing will be skipped"
    )]
    private static partial void LogFailedToAcquireLock(ILogger logger, string processor);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Debug,
        Message = "{Processor} distributed lock acquired"
    )]
    private static partial void LogDistributedLockAcquired(ILogger logger, string processor);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Information,
        Message = "Stopping signal received during {Processor} processing"
    )]
    private static partial void LogStoppingSignalReceived(ILogger logger, string processor);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Warning,
        Message = "Distributed lock was lost during {Processor} processing"
    )]
    private static partial void LogDistributedLockWasLost(ILogger logger, string processor);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Error,
        Message = "Error while processing {Processor} messages"
    )]
    private static partial void LogErrorWhileProcessingMessages(
        ILogger logger,
        Exception ex,
        string processor
    );

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Debug,
        Message = "Waiting for {Processor} trigger or {Delay} timeout"
    )]
    private static partial void LogWaitingForTriggerOrTimeout(
        ILogger logger,
        string processor,
        TimeSpan delay
    );

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Debug,
        Message = "{Processor} triggered immediately via channel"
    )]
    private static partial void LogTriggeredImmediatelyViaChannel(ILogger logger, string processor);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Debug,
        Message = "{Processor} polling interval elapsed"
    )]
    private static partial void LogPollingIntervalElapsed(ILogger logger, string processor);
}
