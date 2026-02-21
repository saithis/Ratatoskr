using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Core;

public class ChannelRegistryTests
{
    [Test]
    public void AddEventPublishChannel_RegistersCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act
        builder.AddEventPublishChannel("local.events", cfg => cfg
            .Produces<TestEvent>()
            .WithRabbitMq(o => o.WithTopicExchange())
        );

        // Assert
        var channel = builder.ChannelRegistry.GetPublishChannel("local.events");
        channel.Should().NotBeNull();
        channel!.Intent.Should().Be(ChannelType.EventPublish);
        channel.Messages.Should().HaveCount(1);
        channel.Messages[0].MessageType.Should().Be(typeof(TestEvent));
    }

    [Test]
    public void FindPublishChannelForTypeName_ReturnsCorrectChannel()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        builder.AddEventPublishChannel("local.events", cfg => cfg.Produces<TestEvent>());

        // Act
        var typeName = "test.event"; // Matches [RatatoskrMessage("test.event")] in TestMessages.cs
        
        var channel = builder.ChannelRegistry.FindPublishChannelForTypeName(typeName);

        // Assert
        channel.Should().NotBeNull();
        channel!.ChannelName.Should().Be("local.events");
    }

    [Test]
    public void FindConsumeChannelsForType_ReturnsResolvableTuples()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        builder.AddCommandConsumeChannel("orders.commands", cfg => cfg
            .Consumes<TestEvent>(m => m.WithRoutingKey("cmd.test"))
        );

        // Act
        var results = builder.ChannelRegistry.FindConsumeChannelsForType("test.event").ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Channel.ChannelName.Should().Be("orders.commands");
        results[0].Message.MessageType.Should().Be(typeof(TestEvent));
    }
}
