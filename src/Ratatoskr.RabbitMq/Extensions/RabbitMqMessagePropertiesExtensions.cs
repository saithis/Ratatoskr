using Ratatoskr.Core;

namespace Ratatoskr.RabbitMq.Extensions;

/// <summary>
/// Extension methods for storing and retrieving RabbitMQ transport metadata (exchange, routing key) on <see cref="MessageProperties"/>.
/// </summary>
public static class RabbitMqMessagePropertiesExtensions
{
    private const string ExchangeExtensionKey = "rabbitmq.exchange";
    private const string RoutingKeyExtensionKey = "rabbitmq.routingKey";

    extension(MessageProperties props)
    {
        /// <summary>
        /// Stores the target exchange name in the transport metadata of these properties.
        /// </summary>
        public MessageProperties SetExchange(string exchange)
        {
            props.TransportMetadata[ExchangeExtensionKey] = exchange;
            return props;
        }

        /// <summary>
        /// Returns the target exchange name stored in the transport metadata, or <see langword="null"/> if not set.
        /// </summary>
        public string? GetExchange() =>
            props.TransportMetadata.TryGetValue(ExchangeExtensionKey, out var exchange)
                ? exchange
                : null;

        /// <summary>
        /// Stores the routing key in the transport metadata of these properties.
        /// </summary>
        public MessageProperties SetRoutingKey(string routingKey)
        {
            props.TransportMetadata[RoutingKeyExtensionKey] = routingKey;
            return props;
        }

        /// <summary>
        /// Returns the routing key stored in the transport metadata, or <see langword="null"/> if not set.
        /// </summary>
        public string? GetRoutingKey() =>
            props.TransportMetadata.TryGetValue(RoutingKeyExtensionKey, out var routingKey)
                ? routingKey
                : null;
    }
}
