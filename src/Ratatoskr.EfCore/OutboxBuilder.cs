using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore;

/// <summary>
/// Builder for configuring the outbox pattern.
/// </summary>
public class OutboxBuilder<TDbContext> where TDbContext : DbContext
{
    internal OutboxOptions Options { get; } = new();
    internal bool RegisterBackgroundService { get; private set; } = true;

    internal OutboxBuilder() { }

    /// <summary>
    /// Sets the polling interval for checking the database for unsent messages.
    /// </summary>
    public OutboxBuilder<TDbContext> WithPollingInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(interval));
        Options.PollingInterval = interval;
        return this;
    }

    /// <summary>
    /// Sets the number of messages to process in each batch.
    /// </summary>
    public OutboxBuilder<TDbContext> WithBatchSize(int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0, nameof(batchSize));
        Options.BatchSize = batchSize;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of retry attempts before marking a message as poisoned.
    /// A value of 0 means the message is poisoned on the first failure.
    /// </summary>
    public OutboxBuilder<TDbContext> WithMaxRetries(int maxRetries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries, nameof(maxRetries));
        Options.MaxRetries = maxRetries;
        return this;
    }

    /// <summary>
    /// Sets the maximum backoff delay between retry attempts.
    /// </summary>
    public OutboxBuilder<TDbContext> WithMaxRetryDelay(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero, nameof(delay));
        Options.MaxRetryDelay = delay;
        return this;
    }

    /// <summary>
    /// Sets how long a message can remain in "processing" state before being considered stuck.
    /// </summary>
    public OutboxBuilder<TDbContext> WithStuckMessageThreshold(TimeSpan threshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero, nameof(threshold));
        Options.StuckMessageThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Sets the delay before restarting the outbox processor after a crash.
    /// </summary>
    public OutboxBuilder<TDbContext> WithRestartDelay(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero, nameof(delay));
        Options.RestartDelay = delay;
        return this;
    }

    /// <summary>
    /// Sets the maximum time to wait when acquiring the distributed lock.
    /// </summary>
    public OutboxBuilder<TDbContext> WithLockAcquireTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(timeout));
        Options.LockAcquireTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets the distributed lock name. Change this if you have multiple outboxes or conflict with the inbox lock.
    /// </summary>
    public OutboxBuilder<TDbContext> WithLockName(string lockName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        Options.LockName = lockName;
        return this;
    }

    /// <summary>
    /// Sets the maximum time a send operation is allowed to run before being cancelled.
    /// Timeout cancellation is treated as a failure and increments the error count.
    /// </summary>
    public OutboxBuilder<TDbContext> WithSendTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(timeout));
        Options.SendTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Prevents the <see cref="Internal.OutboxProcessor{TDbContext}"/> from being registered as a hosted service.
    /// Use this in integration tests where you want deterministic control over when outbox processing runs
    /// (e.g. by calling <c>OutboxMessageProcessor.ProcessBatchAsync</c> directly).
    /// </summary>
    public OutboxBuilder<TDbContext> WithoutBackgroundProcessing()
    {
        RegisterBackgroundService = false;
        return this;
    }

    /// <summary>
    /// Configures all options via an action.
    /// </summary>
    public OutboxBuilder<TDbContext> Configure(Action<OutboxOptions> configure)
    {
        configure(Options);
        return this;
    }
}
