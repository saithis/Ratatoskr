namespace Ratatoskr.EfCore;

/// <summary>
/// Settings shared by the inbox and the outbox: background polling, retries,
/// stuck detection, distributed locking and retention cleanup.
/// </summary>
public abstract class DurableProcessingOptions
{
    private protected DurableProcessingOptions(
        TimeSpan pollingInterval,
        string lockName,
        string cleanupLockName
    )
    {
        PollingInterval = pollingInterval;
        LockName = lockName;
        CleanupLockName = cleanupLockName;
    }

    /// <summary>
    /// How often to poll the database for pending work.
    /// Default: 30 seconds for the inbox, 60 seconds for the outbox.
    /// </summary>
    public TimeSpan PollingInterval { get; set; }

    /// <summary>
    /// How long to wait before restarting after a crash.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum time to wait when acquiring the distributed lock.
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan LockAcquireTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Number of rows (outbox messages or inbox handler statuses) to process per batch.
    /// Default: 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of retries after the first failed attempt before a row is marked as
    /// poisoned, so a row is attempted at most <c>MaxRetries + 1</c> times. 0 poisons on the first failure.
    /// Default: 5.
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// How long a row can be in "processing" state before it's considered stuck and picked up again.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan StuckMessageThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum backoff delay between retry attempts.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Name of the distributed lock held by the background processor.
    /// Default: "InboxProcessor_{DbContextTypeName}" or "OutboxProcessor_{DbContextTypeName}"
    /// (auto-generated per DbContext to avoid collisions).
    /// </summary>
    public string LockName { get; set; }

    /// <summary>
    /// How long to keep successfully processed rows before automatic cleanup deletes them.
    /// Poisoned rows are never auto-deleted; they require manual investigation.
    /// Default: null (automatic cleanup disabled).
    /// </summary>
    public TimeSpan? RetentionPeriod { get; set; }

    /// <summary>
    /// How often the cleanup service runs.
    /// Only applies when <see cref="RetentionPeriod"/> is set.
    /// Default: 1 hour.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum number of rows to delete per cleanup batch to avoid long-running transactions.
    /// Only applies when <see cref="RetentionPeriod"/> is set.
    /// Default: 10000.
    /// </summary>
    public int CleanupBatchSize { get; set; } = 10_000;

    /// <summary>
    /// Name of the distributed lock used by the cleanup service.
    /// Only one instance acquires the lock per cleanup cycle; others skip the cycle.
    /// Default: "InboxCleanup_{DbContextTypeName}" or "OutboxCleanup_{DbContextTypeName}"
    /// (auto-generated per DbContext to avoid collisions).
    /// </summary>
    public string CleanupLockName { get; set; }
}
