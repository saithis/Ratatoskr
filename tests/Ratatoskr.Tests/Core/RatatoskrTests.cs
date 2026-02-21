using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Testing;
using TUnit.Core;

namespace Ratatoskr.Tests.Core;

public class RatatoskrTests
{
    [Test]
    public async Task PublishDirectAsync_WithRegisteredType_SetsCorrectEventType()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTestRatatoskr(bus => bus
            .AddEventPublishChannel("order", c => c.Produces<OrderCreatedEvent>()));
        
        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRatatoskr>();
        var sender = provider.GetRequiredService<InMemoryMessageSender>();
        
        // Act
        await bus.PublishDirectAsync(new OrderCreatedEvent
        {
            OrderId = Guid.NewGuid(),
            Amount = 99.99m,
            CreatedAt = DateTimeOffset.UtcNow
        });
        
        // Assert
        sender.SentMessages.Should().HaveCount(1);
        var message = sender.SentMessages.First();
        message.Properties.Type.Should().Be("order.created");
    }

    [Test]
    public async Task PublishDirectAsync_WithAttributeType_SetsCorrectEventType()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTestRatatoskr();
        
        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRatatoskr>();
        var sender = provider.GetRequiredService<InMemoryMessageSender>();
        
        // Act
        await bus.PublishDirectAsync(new TestEvent { Data = "test data" });
        
        // Assert
        sender.SentMessages.Should().HaveCount(1);
        var message = sender.SentMessages.First();
        message.Properties.Type.Should().Be("test.event");
    }

    [Test]
    public async Task PublishDirectAsync_WithExplicitProperties_UsesProvidedValues()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTestRatatoskr();
        
        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRatatoskr>();
        var sender = provider.GetRequiredService<InMemoryMessageSender>();
        
        var customProperties = new global::Ratatoskr.Core.MessageProperties
        {
            Type = "custom.type",
            Source = "/custom-source",
            Subject = "test-subject"
        };
        
        // Act
        await bus.PublishDirectAsync(new TestEvent { Data = "test" }, customProperties);
        
        // Assert
        sender.SentMessages.Should().HaveCount(1);
        var message = sender.SentMessages.First();
        message.Properties.Type.Should().Be("custom.type");
        message.Properties.Source.Should().Be("/custom-source");
        message.Properties.Subject.Should().Be("test-subject");
    }

    [Test]
    public async Task PublishDirectAsync_WithExtensions_IncludesExtensionsInProperties()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTestRatatoskr(bus => bus
            .AddEventPublishChannel("test", c => c.Produces<TestEvent>(m => m
                .WithRoutingKey("routeKey"))));
        
        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRatatoskr>();
        var sender = provider.GetRequiredService<InMemoryMessageSender>();
        
        // Act
        await bus.PublishDirectAsync(new TestEvent { Data = "test" });
        
        // Assert
        sender.SentMessages.Should().HaveCount(1);
        var message = sender.SentMessages.First();
        message.Properties.TransportMetadata.Should().ContainKey("rabbitmq.routingKey");
        message.Properties.TransportMetadata["rabbitmq.routingKey"].Should().Be("routeKey");
    }

    [Test]
    public async Task PublishDirectAsync_SetsTimestamp()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTestRatatoskr();
        
        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRatatoskr>();
        var sender = provider.GetRequiredService<InMemoryMessageSender>();
        
        var beforePublish = DateTimeOffset.UtcNow;
        
        // Act
        await bus.PublishDirectAsync(new TestEvent { Data = "test" });
        
        var afterPublish = DateTimeOffset.UtcNow;
        
        // Assert
        sender.SentMessages.Should().HaveCount(1);
        var message = sender.SentMessages.First();
        message.Properties.Time.Should().NotBeNull();
        message.Properties.Time!.Value.Should().BeOnOrAfter(beforePublish);
        message.Properties.Time!.Value.Should().BeOnOrBefore(afterPublish);
    }
}
