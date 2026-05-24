using Ratatoskr.Core;

namespace Ratatoskr.RabbitMq.Extensions;

public static class RabbitMqMessagePropertiesExtensions
{
    private const string ExchangeExtensionKey = "rabbitmq.exchange";
    private const string RoutingKeyExtensionKey = "rabbitmq.routingKey";

    extension(MessageProperties props)
    {
        public MessageProperties SetExchange(string exchange)
        {
            props.TransportMetadata[ExchangeExtensionKey] = exchange;
            return props;
        }

        public string? GetExchange() =>
            props.TransportMetadata.TryGetValue(ExchangeExtensionKey, out var exchange)
                ? exchange
                : null;

        public MessageProperties SetRoutingKey(string routingKey)
        {
            props.TransportMetadata[RoutingKeyExtensionKey] = routingKey;
            return props;
        }

        public string? GetRoutingKey() =>
            props.TransportMetadata.TryGetValue(RoutingKeyExtensionKey, out var routingKey)
                ? routingKey
                : null;
    }
}
