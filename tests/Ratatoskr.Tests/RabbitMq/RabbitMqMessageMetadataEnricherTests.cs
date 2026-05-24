using AwesomeAssertions;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.RabbitMq;

public class RabbitMqMessageMetadataEnricherTests
{
    private readonly RabbitMqMessageMetadataEnricher _enricher = new();

    [Test]
    public void Enrich_SetsExchangeFromChannelName()
    {
        // Arrange
        var channel = new ChannelRegistration("orders.events", ChannelType.EventPublish);
        var message = new MessageRegistration(typeof(TestEvent), "test.event");
        channel.Messages.Add(message);
        var publishInfo = new PublishInformation { Channel = channel, Message = message };
        var properties = new MessageProperties();

        // Act
        _enricher.Enrich(publishInfo, properties);

        // Assert
        properties.GetExchange().Should().Be("orders.events");
    }

    [Test]
    public void Enrich_SetsRoutingKeyFromMessageTypeName()
    {
        // Arrange
        var channel = new ChannelRegistration("orders.events", ChannelType.EventPublish);
        var message = new MessageRegistration(typeof(TestEvent), "test.event");
        channel.Messages.Add(message);
        var publishInfo = new PublishInformation { Channel = channel, Message = message };
        var properties = new MessageProperties();

        // Act
        _enricher.Enrich(publishInfo, properties);

        // Assert
        properties.GetRoutingKey().Should().Be("test.event");
    }

    [Test]
    public void Enrich_WithExplicitRoutingKey_UsesConfiguredRoutingKey()
    {
        // Arrange
        var channel = new ChannelRegistration("orders.events", ChannelType.EventPublish);
        var message = new MessageRegistration(typeof(TestEvent), "test.event");
        message.SetExtension(new RabbitMqMessageOptions { RoutingKey = "custom.routing.key" });
        channel.Messages.Add(message);
        var publishInfo = new PublishInformation { Channel = channel, Message = message };
        var properties = new MessageProperties();

        // Act
        _enricher.Enrich(publishInfo, properties);

        // Assert
        properties.GetRoutingKey().Should().Be("custom.routing.key");
    }

    [Test]
    public void Enrich_WithNoRabbitMqOptions_FallsBackToMessageTypeName()
    {
        // Arrange
        var channel = new ChannelRegistration("orders.events", ChannelType.EventPublish);
        var message = new MessageRegistration(typeof(OrderCreatedEvent), "order.created");
        channel.Messages.Add(message);
        var publishInfo = new PublishInformation { Channel = channel, Message = message };
        var properties = new MessageProperties();

        // Act
        _enricher.Enrich(publishInfo, properties);

        // Assert
        properties.GetRoutingKey().Should().Be("order.created");
        properties.GetExchange().Should().Be("orders.events");
    }
}
