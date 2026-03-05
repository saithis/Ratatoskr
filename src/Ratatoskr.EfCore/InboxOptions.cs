namespace Ratatoskr.EfCore;

/// <summary>
/// Configuration options for the inbox pattern.
/// </summary>
public class InboxOptions : ProcessorOptionsBase
{
    /// <summary>
    /// Section name in configuration files.
    /// </summary>
    public const string SectionName = "Ratatoskr:Inbox";

    /// <summary>
    /// Creates a new instance with inbox-specific defaults.
    /// </summary>
    public InboxOptions()
    {
        PollingInterval = TimeSpan.FromSeconds(30);
        LockName = "InboxProcessor";
    }

    /// <summary>
    /// Maximum time a handler is allowed to run before being cancelled.
    /// When set, the handler receives a cancellation token that fires after this duration.
    /// Timeout cancellation is treated as a handler failure (increments ErrorCount) and
    /// will eventually lead to poisoning after MaxRetries.
    /// Default: null (no timeout).
    /// </summary>
    public TimeSpan? HandlerTimeout { get; set; }
}
