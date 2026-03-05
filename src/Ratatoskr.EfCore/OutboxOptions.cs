namespace Ratatoskr.EfCore;

/// <summary>
/// Configuration options for the outbox pattern.
/// </summary>
public class OutboxOptions : ProcessorOptionsBase
{
    /// <summary>
    /// Section name in configuration files.
    /// </summary>
    public const string SectionName = "Ratatoskr:Outbox";

    /// <summary>
    /// Creates a new instance with outbox-specific defaults.
    /// </summary>
    public OutboxOptions()
    {
        PollingInterval = TimeSpan.FromSeconds(60);
        LockName = "OutboxProcessor";
    }

    /// <summary>
    /// Maximum time a send operation is allowed to run before being cancelled.
    /// When set, the send receives a cancellation token that fires after this duration.
    /// Timeout cancellation is treated as a failure (increments ErrorCount) and
    /// will eventually lead to poisoning after MaxRetries.
    /// Default: null (no timeout).
    /// </summary>
    public TimeSpan? SendTimeout { get; set; }
}
