namespace Ratatoskr.EfCore;

/// <summary>
/// Configuration options for the outbox pattern.
/// </summary>
public class OutboxOptions
{
    /// <summary>
    /// Section name in configuration files.
    /// </summary>
    public const string SectionName = "Ratatoskr:Outbox";
    
    /// <summary>
    /// How often to poll the database for unsent messages.
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(60);
    
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
    /// Number of messages to process in each batch.
    /// Default: 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;
    
    /// <summary>
    /// Maximum number of retry attempts before marking a message as poisoned.
    /// Default: 5.
    /// </summary>
    public int MaxRetries { get; set; } = 5;
    
    /// <summary>
    /// How long a message can be in "processing" state before it's considered stuck.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan StuckMessageThreshold { get; set; } = TimeSpan.FromMinutes(5);
    
    /// <summary>
    /// Maximum backoff delay between retries.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
    
    /// <summary>
    /// Name of the distributed lock. Change this if you have multiple outboxes.
    /// Default: "OutboxProcessor".
    /// </summary>
    public string LockName { get; set; } = "OutboxProcessor";

    /// <summary>
    /// Maximum time a send operation is allowed to run before being cancelled.
    /// When set, the send receives a cancellation token that fires after this duration.
    /// Timeout cancellation is treated as a failure (increments ErrorCount) and
    /// will eventually lead to poisoning after MaxRetries.
    /// Default: null (no timeout).
    /// </summary>
    public TimeSpan? SendTimeout { get; set; }
}
