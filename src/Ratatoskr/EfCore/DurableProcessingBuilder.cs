namespace Ratatoskr.EfCore;

/// <summary>
/// Builder methods shared by <see cref="InboxBuilder{TDbContext}"/> and <see cref="OutboxBuilder{TDbContext}"/>.
/// </summary>
/// <typeparam name="TBuilder">The concrete builder type, returned from every method for chaining.</typeparam>
/// <typeparam name="TOptions">The options type configured by the builder.</typeparam>
public abstract class DurableProcessingBuilder<TBuilder, TOptions>
    where TBuilder : DurableProcessingBuilder<TBuilder, TOptions>
    where TOptions : DurableProcessingOptions, new()
{
    internal TOptions Options { get; } = new();
    internal bool RegisterBackgroundService { get; private set; } = true;

    private protected DurableProcessingBuilder() { }

    private TBuilder Self => (TBuilder)this;

    /// <summary>
    /// Sets the polling interval for checking the database for pending work.
    /// </summary>
    public TBuilder WithPollingInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        Options.PollingInterval = interval;
        return Self;
    }

    /// <summary>
    /// Sets the number of rows to process in each batch.
    /// </summary>
    public TBuilder WithBatchSize(int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);
        Options.BatchSize = batchSize;
        return Self;
    }

    /// <summary>
    /// Sets the maximum backoff delay between retry attempts.
    /// </summary>
    public TBuilder WithMaxRetryDelay(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
        Options.MaxRetryDelay = delay;
        return Self;
    }

    /// <summary>
    /// Sets how long a row can remain in "processing" state before being considered stuck.
    /// </summary>
    public TBuilder WithStuckMessageThreshold(TimeSpan threshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero);
        Options.StuckMessageThreshold = threshold;
        return Self;
    }

    /// <summary>
    /// Sets the delay before restarting the background processor after a crash.
    /// </summary>
    public TBuilder WithRestartDelay(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
        Options.RestartDelay = delay;
        return Self;
    }

    /// <summary>
    /// Sets the maximum time to wait when acquiring the distributed lock.
    /// </summary>
    public TBuilder WithLockAcquireTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Options.LockAcquireTimeout = timeout;
        return Self;
    }

    /// <summary>
    /// Sets the distributed lock name of the background processor.
    /// The default is already unique per DbContext and per inbox/outbox.
    /// </summary>
    public TBuilder WithLockName(string lockName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        Options.LockName = lockName;
        return Self;
    }

    /// <summary>
    /// Enables automatic cleanup of successfully processed rows older than the specified retention period.
    /// Poisoned rows are never auto-deleted; they require manual investigation.
    /// </summary>
    public TBuilder WithRetention(TimeSpan period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        Options.RetentionPeriod = period;
        return Self;
    }

    /// <summary>
    /// Sets how often the cleanup service runs. Only applies when <see cref="WithRetention"/> is configured.
    /// </summary>
    public TBuilder WithCleanupInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        Options.CleanupInterval = interval;
        return Self;
    }

    /// <summary>
    /// Sets the maximum number of rows to delete per cleanup batch.
    /// Only applies when <see cref="WithRetention"/> is configured.
    /// </summary>
    public TBuilder WithCleanupBatchSize(int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);
        Options.CleanupBatchSize = batchSize;
        return Self;
    }

    /// <summary>
    /// Sets the distributed lock name for the cleanup service.
    /// Only one instance acquires the lock per cleanup cycle; others skip the cycle.
    /// </summary>
    public TBuilder WithCleanupLockName(string lockName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        Options.CleanupLockName = lockName;
        return Self;
    }

    /// <summary>
    /// Configures all options via an action.
    /// </summary>
    public TBuilder Configure(Action<TOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Options);
        return Self;
    }

    /// <summary>
    /// Prevents the background processor from being registered as a hosted service.
    /// Use this in integration tests where you want deterministic control over when processing runs
    /// (e.g. by calling <c>ProcessBatchAsync</c> on the message processor directly).
    /// Note: this also prevents the cleanup service from running,
    /// even when <see cref="WithRetention"/> is configured.
    /// </summary>
    public TBuilder WithoutBackgroundProcessing()
    {
        RegisterBackgroundService = false;
        return Self;
    }
}
