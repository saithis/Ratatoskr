namespace Ratatoskr.CloudEvents;

/// <summary>
/// CloudEvents content modes.
/// </summary>
public enum CloudEventsContentMode
{
    /// <summary>
    /// Data is serialized directly, CloudEvents attributes are in AMQP application-properties.
    /// More efficient for AMQP brokers. Uses cloudEvents_ prefix per AMQP binding spec.
    /// </summary>
    Binary,

    /// <summary>
    /// Entire CloudEvent is serialized as JSON including envelope and data.
    /// Content-Type: application/cloudevents+json
    /// </summary>
    Structured,
}
