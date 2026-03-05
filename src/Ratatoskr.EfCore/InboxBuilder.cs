using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore;

/// <summary>
/// Builder for configuring the inbox pattern.
/// </summary>
public class InboxBuilder<TDbContext> where TDbContext : DbContext, IInboxDbContext
{
    internal InboxOptions Options { get; } = new();
    internal bool RegisterBackgroundService { get; private set; } = true;

    internal InboxBuilder() { }

    /// <summary>
    /// Sets the polling interval for checking the database for pending handler deliveries.
    /// </summary>
    public InboxBuilder<TDbContext> WithPollingInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(interval));
        Options.PollingInterval = interval;
        return this;
    }

    /// <summary>
    /// Sets the number of handler statuses to process in each batch.
    /// </summary>
    public InboxBuilder<TDbContext> WithBatchSize(int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0, nameof(batchSize));
        Options.BatchSize = batchSize;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of delivery attempts before marking a handler as poisoned.
    /// A value of 1 means the handler runs once and is poisoned on the first failure.
    /// </summary>
    public InboxBuilder<TDbContext> WithMaxRetries(int maxRetries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxRetries, 0, nameof(maxRetries));
        Options.MaxRetries = maxRetries;
        return this;
    }

    /// <summary>
    /// Sets the maximum backoff delay between retry attempts.
    /// </summary>
    public InboxBuilder<TDbContext> WithMaxRetryDelay(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero, nameof(delay));
        Options.MaxRetryDelay = delay;
        return this;
    }

    /// <summary>
    /// Sets how long a handler status can remain in "processing" state before being considered stuck.
    /// </summary>
    public InboxBuilder<TDbContext> WithStuckMessageThreshold(TimeSpan threshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero, nameof(threshold));
        Options.StuckMessageThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Sets the delay before restarting the inbox processor after a crash.
    /// </summary>
    public InboxBuilder<TDbContext> WithRestartDelay(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero, nameof(delay));
        Options.RestartDelay = delay;
        return this;
    }

    /// <summary>
    /// Sets the maximum time to wait when acquiring the distributed lock.
    /// </summary>
    public InboxBuilder<TDbContext> WithLockAcquireTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(timeout));
        Options.LockAcquireTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets the distributed lock name. Change this if you have multiple inboxes or conflict with the outbox lock.
    /// </summary>
    public InboxBuilder<TDbContext> WithLockName(string lockName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        Options.LockName = lockName;
        return this;
    }

    /// <summary>
    /// Sets the maximum time a handler is allowed to run before being cancelled.
    /// Timeout cancellation is treated as a handler failure and increments the error count.
    /// </summary>
    public InboxBuilder<TDbContext> WithHandlerTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(timeout));
        Options.HandlerTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets how long to keep fully completed inbox messages before automatic cleanup.
    /// Default: 7 days. Set to null to disable cleanup of completed messages.
    /// </summary>
    public InboxBuilder<TDbContext> WithCompletedRetention(TimeSpan? retention)
    {
        if (retention.HasValue)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention.Value, TimeSpan.Zero, nameof(retention));
        Options.CompletedRetention = retention;
        return this;
    }

    /// <summary>
    /// Sets how long to keep poisoned inbox messages before automatic cleanup.
    /// Default: 30 days. Set to null to disable cleanup of poisoned messages.
    /// </summary>
    public InboxBuilder<TDbContext> WithPoisonedRetention(TimeSpan? retention)
    {
        if (retention.HasValue)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention.Value, TimeSpan.Zero, nameof(retention));
        Options.PoisonedRetention = retention;
        return this;
    }

    /// <summary>
    /// Sets how often the cleanup processor runs.
    /// Default: 1 hour.
    /// </summary>
    public InboxBuilder<TDbContext> WithCleanupInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(interval));
        Options.CleanupInterval = interval;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of messages to delete per cleanup batch.
    /// Default: 1000.
    /// </summary>
    public InboxBuilder<TDbContext> WithCleanupBatchSize(int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0, nameof(batchSize));
        Options.CleanupBatchSize = batchSize;
        return this;
    }

    /// <summary>
    /// Disables automatic cleanup of completed and poisoned inbox messages.
    /// </summary>
    public InboxBuilder<TDbContext> WithoutCleanup()
    {
        Options.CompletedRetention = null;
        Options.PoisonedRetention = null;
        return this;
    }

    /// <summary>
    /// Prevents the <see cref="InboxProcessor{TDbContext}"/> from being registered as a hosted service.
    /// Use this in integration tests where you want deterministic control over when inbox processing runs
    /// (e.g. by calling <c>InboxMessageProcessor.ProcessBatchAsync</c> directly).
    /// </summary>
    public InboxBuilder<TDbContext> WithoutBackgroundProcessing()
    {
        RegisterBackgroundService = false;
        return this;
    }
}
