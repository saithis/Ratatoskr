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
            .Consumes<TestEvent>(
                m => m.WithHandler<TestEventHandler>(),
                msg => msg.WithRoutingKey("cmd.test"))
        );

        // Act
        var results = builder.ChannelRegistry.FindConsumeChannelsForType("test.event").ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Channel.ChannelName.Should().Be("orders.commands");
        results[0].Message.MessageType.Should().Be(typeof(TestEvent));
    }

    [Test]
    public void Freeze_PreventsFurtherRegistration()
    {
        // Arrange
        var registry = new ChannelRegistry();
        registry.Register(new ChannelRegistration("existing.channel", ChannelType.EventPublish));
        registry.Freeze();

        // Act
        var act = () => registry.Register(new ChannelRegistration("new.channel", ChannelType.EventPublish));

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*frozen*");
    }

    [Test]
    public void GetAllChannels_ReturnsPublishAndConsumeChannels()
    {
        // Arrange
        var registry = new ChannelRegistry();
        registry.Register(new ChannelRegistration("pub.channel", ChannelType.EventPublish));
        registry.Register(new ChannelRegistration("consume.channel", ChannelType.EventConsume));

        // Act
        var allChannels = registry.GetAllChannels().ToList();

        // Assert
        allChannels.Should().HaveCount(2);
        allChannels.Should().Contain(c => c.ChannelName == "pub.channel");
        allChannels.Should().Contain(c => c.ChannelName == "consume.channel");
    }

    [Test]
    public void FindPublishChannelForMessage_ReturnsCorrectChannel()
    {
        // Arrange
        var registry = new ChannelRegistry();
        var channel = new ChannelRegistration("test.exchange", ChannelType.EventPublish);
        channel.Messages.Add(new MessageRegistration(typeof(TestEvent), "test.event"));
        registry.Register(channel);

        // Act
        var result = registry.FindPublishChannelForMessage(typeof(TestEvent));

        // Assert
        result.Should().NotBeNull();
        result!.ChannelName.Should().Be("test.exchange");
    }

    [Test]
    public void GetPublishInformation_ReturnsChannelAndMessage()
    {
        // Arrange
        var registry = new ChannelRegistry();
        var channel = new ChannelRegistration("test.exchange", ChannelType.EventPublish);
        channel.Messages.Add(new MessageRegistration(typeof(TestEvent), "test.event"));
        registry.Register(channel);

        // Act
        var result = registry.GetPublishInformation(typeof(TestEvent));

        // Assert
        result.Should().NotBeNull();
        result!.Channel.ChannelName.Should().Be("test.exchange");
        result.Message.MessageTypeName.Should().Be("test.event");
        result.Message.MessageType.Should().Be(typeof(TestEvent));
    }

    [Test]
    public void GetPublishChannel_NonExistent_ReturnsNull()
    {
        // Arrange
        var registry = new ChannelRegistry();

        // Act
        var result = registry.GetPublishChannel("nonexistent.channel");

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void Register_DuplicateChannelName_Throws()
    {
        // Arrange
        var registry = new ChannelRegistry();
        registry.Register(new ChannelRegistration("duplicate.channel", ChannelType.EventPublish));

        // Act
        var act = () => registry.Register(new ChannelRegistration("duplicate.channel", ChannelType.EventPublish));

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already registered*");
    }

    [Test]
    public void GetPublishInformation_NonExistent_ReturnsNull()
    {
        // Arrange
        var registry = new ChannelRegistry();

        // Act
        var result = registry.GetPublishInformation(typeof(TestEvent));

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void GetPublishChannels_ReturnsOnlyPublishChannels()
    {
        // Arrange
        var registry = new ChannelRegistry();
        registry.Register(new ChannelRegistration("pub.channel", ChannelType.EventPublish));
        registry.Register(new ChannelRegistration("consume.channel", ChannelType.EventConsume));

        // Act
        var publishChannels = registry.GetPublishChannels().ToList();

        // Assert
        publishChannels.Should().HaveCount(1);
        publishChannels[0].ChannelName.Should().Be("pub.channel");
    }

    [Test]
    public void GetConsumeChannels_ReturnsOnlyConsumeChannels()
    {
        // Arrange
        var registry = new ChannelRegistry();
        registry.Register(new ChannelRegistration("pub.channel", ChannelType.EventPublish));
        registry.Register(new ChannelRegistration("consume.channel", ChannelType.EventConsume));

        // Act
        var consumeChannels = registry.GetConsumeChannels().ToList();

        // Assert
        consumeChannels.Should().HaveCount(1);
        consumeChannels[0].ChannelName.Should().Be("consume.channel");
    }
}
