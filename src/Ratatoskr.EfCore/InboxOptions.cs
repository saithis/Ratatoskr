namespace Ratatoskr.EfCore;

/// <summary>
/// Configuration options for the inbox pattern.
/// </summary>
public class InboxOptions
{
    /// <summary>
    /// Section name in configuration files.
    /// </summary>
    public const string SectionName = "Ratatoskr:Inbox";

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

    /// <summary>
    /// Name of the distributed lock. Change this if you have multiple inboxes or conflict with the outbox lock.
    /// Default: "InboxProcessor".
    /// </summary>
    public string LockName { get; set; } = "InboxProcessor";

    /// <summary>
    /// Maximum time a handler is allowed to run before being cancelled.
    /// When set, the handler receives a cancellation token that fires after this duration.
    /// Timeout cancellation is treated as a handler failure (increments ErrorCount) and
    /// will eventually lead to poisoning after MaxRetries.
    /// Default: null (no timeout).
    /// </summary>
    public TimeSpan? HandlerTimeout { get; set; }

    /// <summary>
    /// How long to keep fully completed inbox messages (all handlers completed, none poisoned)
    /// before automatic cleanup. Set to null to disable cleanup of completed messages.
    /// Default: 7 days.
    /// </summary>
    public TimeSpan? CompletedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long to keep poisoned inbox messages (at least one handler poisoned, all terminal)
    /// before automatic cleanup. Set to null to disable cleanup of poisoned messages.
    /// Default: 30 days.
    /// </summary>
    public TimeSpan? PoisonedRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How often the cleanup processor runs to delete old completed/poisoned messages.
    /// Default: 1 hour.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}
