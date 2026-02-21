namespace Ratatoskr.RabbitMq.Config;

/// <summary>
/// Configures retry behavior for failed message processing.
/// Messages that exceed the maximum retry count are routed to a dead-letter queue.
/// </summary>
public class RetryOptions
{
    /// <summary>
    /// Maximum number of retry attempts before sending to the dead-letter queue. Default: 3.
    /// </summary>
    public int MaxRetries { get; private set; } = 3;

    /// <summary>
    /// Delay between retry attempts, used as the TTL on the retry queue. Default: 30 seconds.
    /// </summary>
    public TimeSpan Delay { get; private set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether Ratatoskr should automatically provision retry and dead-letter queue topology. Default: true.
    /// </summary>
    public bool UseManaged { get; private set; } = true;

    /// <summary>
    /// Suffix appended to the queue name to form the dead-letter queue name. Default: ".dlq".
    /// </summary>
    public string DeadLetterSuffix { get; private set; } = ".dlq";

    /// <summary>
    /// Suffix appended to the queue name to form the retry queue name. Default: ".retry".
    /// </summary>
    public string RetrySuffix { get; private set; } = ".retry";

    /// <summary>Sets the maximum number of retry attempts.</summary>
    public RetryOptions WithMaxRetries(int maxRetries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        MaxRetries = maxRetries;
        return this;
    }

    /// <summary>Sets the delay between retry attempts.</summary>
    public RetryOptions WithDelay(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
        Delay = delay;
        return this;
    }

    /// <summary>Enables or disables automatic retry/DLQ topology provisioning.</summary>
    public RetryOptions WithManaged(bool useManaged = true)
    {
        UseManaged = useManaged;
        return this;
    }

    /// <summary>Sets the suffix for the dead-letter queue name.</summary>
    public RetryOptions WithDeadLetterSuffix(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        DeadLetterSuffix = suffix;
        return this;
    }

    /// <summary>Sets the suffix for the retry queue name.</summary>
    public RetryOptions WithRetrySuffix(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        RetrySuffix = suffix;
        return this;
    }
}
