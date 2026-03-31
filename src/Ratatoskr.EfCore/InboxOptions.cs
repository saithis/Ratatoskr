namespace Ratatoskr.EfCore;

/// <summary>
/// Configuration options for the inbox pattern.
/// </summary>
public class InboxOptions
{
    /// <summary>
    /// How often to poll the database for pending handler deliveries.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(30);

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
    /// Number of handler statuses to process per batch.
    /// Default: 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of retry attempts before marking a handler status as poisoned.
    /// Default: 5.
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// How long a handler status can be in "processing" state before it's considered stuck.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan StuckMessageThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum backoff delay between handler retry attempts.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    internal const string DefaultLockName = "InboxProcessor";

    /// <summary>
    /// Name of the distributed lock.
    /// Default: "InboxProcessor_{DbContextTypeName}" (auto-generated per DbContext to avoid collisions).
    /// </summary>
    public string LockName { get; set; } = DefaultLockName;

    /// <summary>
    /// Maximum time a handler is allowed to run before being cancelled.
    /// When set, the handler receives a cancellation token that fires after this duration.
    /// Timeout cancellation is treated as a handler failure (increments ErrorCount) and
    /// will eventually lead to poisoning after MaxRetries.
    /// Default: null (no timeout).
    /// </summary>
    public TimeSpan? HandlerTimeout { get; set; }

    /// <summary>
    /// How long to keep completed handler statuses before automatic cleanup deletes them.
    /// Poisoned statuses are never auto-deleted — they require manual investigation.
    /// Orphaned inbox messages (with no remaining handler statuses) are also deleted.
    /// Default: null (automatic cleanup disabled).
    /// </summary>
    public TimeSpan? RetentionPeriod { get; set; }

    /// <summary>
    /// How often the cleanup service runs to delete old completed handler statuses.
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

    internal const string DefaultCleanupLockName = "InboxCleanup";

    /// <summary>
    /// Name of the distributed lock used by the cleanup service.
    /// Only one instance acquires the lock per cleanup cycle; others skip the cycle.
    /// Default: "InboxCleanup_{DbContextTypeName}" (auto-generated per DbContext to avoid collisions).
    /// </summary>
    public string CleanupLockName { get; set; } = DefaultCleanupLockName;
}
