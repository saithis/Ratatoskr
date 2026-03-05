namespace Ratatoskr.EfCore;

/// <summary>
/// Base class for processor options shared between inbox and outbox.
/// Contains common configuration for polling, retries, locking, and cleanup.
/// </summary>
public abstract class ProcessorOptionsBase
{
    /// <summary>
    /// How often to poll the database for pending work.
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
    /// Number of items to process per batch.
    /// Default: 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of retry attempts before marking as poisoned.
    /// Default: 5.
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// How long an item can be in "processing" state before it's considered stuck.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan StuckMessageThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum backoff delay between retry attempts.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Name of the distributed lock.
    /// </summary>
    public string LockName { get; set; } = "";

    /// <summary>
    /// How long to keep completed messages before automatic cleanup.
    /// Set to null to disable cleanup of completed messages.
    /// Default: 7 days.
    /// </summary>
    public TimeSpan? CompletedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long to keep poisoned messages before automatic cleanup.
    /// Set to null to disable cleanup of poisoned messages.
    /// Default: 30 days.
    /// </summary>
    public TimeSpan? PoisonedRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How often the cleanup processor runs.
    /// Default: 1 hour.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum number of messages to delete per cleanup batch.
    /// Cleanup runs in a loop, deleting up to this many messages at a time until no more match.
    /// Default: 1000.
    /// </summary>
    public int CleanupBatchSize { get; set; } = 1000;
}
