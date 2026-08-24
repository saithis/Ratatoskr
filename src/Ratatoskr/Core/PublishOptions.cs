namespace Ratatoskr.Core;

/// <summary>
/// Options for configuring message publish parameters, including metadata and deferred delivery schedules.
/// </summary>
public sealed class PublishOptions
{
    /// <summary>
    /// Gets the underlying message properties.
    /// </summary>
    public MessageProperties Properties { get; } = new();

    /// <summary>
    /// Delivery timestamp when this message should be processed/delivered.
    /// Null indicates immediate delivery.
    /// </summary>
    public DateTimeOffset? ScheduledAt
    {
        get => Properties.ScheduledAt;
        set => Properties.ScheduledAt = value;
    }

    /// <summary>
    /// Schedules the message for delivery at a specific point in time.
    /// </summary>
    /// <param name="deliverAt">The absolute timestamp when delivery should occur.</param>
    /// <returns>This options instance for fluent chaining.</returns>
    public PublishOptions DeliverAt(DateTimeOffset deliverAt)
    {
        ScheduledAt = deliverAt;
        return this;
    }

    /// <summary>
    /// Schedules the message for delivery after a specific delay.
    /// </summary>
    /// <param name="delay">The duration to wait before delivery.</param>
    /// <param name="timeProvider">Optional time provider (defaults to <see cref="TimeProvider.System"/>).</param>
    /// <returns>This options instance for fluent chaining.</returns>
    public PublishOptions DeliverAfter(TimeSpan delay, TimeProvider? timeProvider = null)
    {
        var provider = timeProvider ?? TimeProvider.System;
        ScheduledAt = provider.GetUtcNow().Add(delay);
        return this;
    }
}
