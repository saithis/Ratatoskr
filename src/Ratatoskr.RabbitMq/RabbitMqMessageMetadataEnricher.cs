using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq;

public class RabbitMqMessageMetadataEnricher : ITransportMessageMetadataEnricher
{
    public string TransportName => "rabbitmq";

    public void Enrich(PublishInformation publishInformation, MessageProperties properties)
    {
        var messageOptions = publishInformation.Message.GetRabbitMqOptions();
        properties.SetExchange(publishInformation.Channel.ChannelName);
        properties.SetRoutingKey(messageOptions?.RoutingKey ?? publishInformation.Message.MessageTypeName);
    }
}