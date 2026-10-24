namespace Ratatoskr.EfCore;

/// <summary>
/// Configuration options for the outbox pattern.
/// </summary>
public class OutboxOptions : DurableProcessingOptions
{
    internal const string DefaultLockName = "OutboxProcessor";
    internal const string DefaultCleanupLockName = "OutboxCleanup";

    /// <summary>
    /// Creates outbox options with the default settings.
    /// </summary>
    public OutboxOptions()
        : base(TimeSpan.FromSeconds(60), DefaultLockName, DefaultCleanupLockName) { }

    /// <summary>
    /// Maximum time a send operation is allowed to run before being cancelled.
    /// When set, the send receives a cancellation token that fires after this duration.
    /// Timeout cancellation is treated as a failure (increments ErrorCount) and
    /// will eventually lead to poisoning after MaxRetries.
    /// Default: null (no timeout).
    /// </summary>
    public TimeSpan? SendTimeout { get; set; }

    /// <summary>
    /// Maximum allowed size of the serialized message body in bytes.
    /// Messages exceeding this limit will cause <c>SaveChangesAsync</c> to throw
    /// an <see cref="InvalidOperationException"/>, rolling back the entire transaction.
    /// Default: null (no limit).
    /// </summary>
    public int? MaxMessageSize { get; set; }
}
