namespace Ratatoskr.RabbitMq.Config;

/// <summary>
/// Per-message RabbitMQ options, such as a custom routing key override.
/// </summary>
public class RabbitMqMessageOptions
{
    /// <summary>
    /// Routing key used when publishing this message type. Defaults to the message type name when not set.
    /// </summary>
    public string? RoutingKey { get; set; }

    /// <summary>
    /// Sets <see cref="RoutingKey"/> and returns this instance for fluent chaining.
    /// </summary>
    public RabbitMqMessageOptions WithRoutingKey(string routingKey)
    {
        RoutingKey = routingKey;
        return this;
    }
}
