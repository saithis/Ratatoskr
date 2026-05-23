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
            props.TransportMetadata.GetValueOrDefault(ExchangeExtensionKey);

        public MessageProperties SetRoutingKey(string routingKey)
        {
            props.TransportMetadata[RoutingKeyExtensionKey] = routingKey;
            return props;
        }

        public string? GetRoutingKey() =>
            props.TransportMetadata.GetValueOrDefault(RoutingKeyExtensionKey);
    }
}
