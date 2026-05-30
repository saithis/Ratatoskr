using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Enriches outgoing message properties with RabbitMQ transport metadata (exchange and routing key) before publishing.
/// </summary>
public class RabbitMqMessageMetadataEnricher : ITransportMessageMetadataEnricher
{
    /// <summary>
    /// Returns the transport name this enricher applies to.
    /// </summary>
    public string TransportName => RabbitMqConstants.TransportName;

    /// <summary>
    /// Sets the target exchange and routing key on <paramref name="properties"/> based on the channel and message registration.
    /// </summary>
    public void Enrich(PublishInformation publishInformation, MessageProperties properties)
    {
        ArgumentNullException.ThrowIfNull(publishInformation);
        ArgumentNullException.ThrowIfNull(properties);

        var messageOptions = publishInformation.Message.GetRabbitMqOptions();
        properties.SetExchange(publishInformation.Channel.ChannelName);
        properties.SetRoutingKey(
            messageOptions?.RoutingKey ?? publishInformation.Message.MessageTypeName
        );
    }
}
