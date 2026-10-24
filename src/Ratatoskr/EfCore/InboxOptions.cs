namespace Ratatoskr.EfCore;

/// <summary>
/// Configuration options for the inbox pattern.
/// </summary>
/// <remarks>
/// <see cref="DurableProcessingOptions.RetentionPeriod"/> applies to completed handler statuses.
/// Orphaned inbox messages (with no remaining handler statuses) are deleted by the same cleanup.
/// </remarks>
public class InboxOptions : DurableProcessingOptions
{
    internal const string DefaultLockName = "InboxProcessor";
    internal const string DefaultCleanupLockName = "InboxCleanup";

    /// <summary>
    /// Creates inbox options with the default settings.
    /// </summary>
    public InboxOptions()
        : base(TimeSpan.FromSeconds(30), DefaultLockName, DefaultCleanupLockName) { }

    /// <summary>
    /// Controls whether consume channels are required to configure <c>UseInbox&lt;TDbContext&gt;()</c>.
    /// Default: <see cref="ConsumeChannelInboxRequirement.None"/>.
    /// </summary>
    public ConsumeChannelInboxRequirement ConsumeChannelInboxRequirement { get; set; } =
        ConsumeChannelInboxRequirement.None;

    /// <summary>
    /// Maximum time a handler is allowed to run before being cancelled.
    /// When set, the handler receives a cancellation token that fires after this duration.
    /// Timeout cancellation is treated as a handler failure (increments ErrorCount) and
    /// will eventually lead to poisoning after MaxRetries.
    /// Default: null (no timeout).
    /// </summary>
    public TimeSpan? HandlerTimeout { get; set; }
}
