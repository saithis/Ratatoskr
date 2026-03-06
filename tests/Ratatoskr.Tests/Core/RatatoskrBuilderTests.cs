using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr;
using Ratatoskr.Core;
using TUnit.Core;

namespace Ratatoskr.Tests.Core;

public class RatatoskrBuilderTests
{
    [Test]
    public void AddEventConsumeChannel_RegistersChannel()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        
        // Act
        builder.AddEventConsumeChannel("test-channel", _ => {});
        
        // Assert
        var channel = builder.ChannelRegistry.GetConsumeChannel("test-channel");
        channel.Should().NotBeNull();
        channel!.Intent.Should().Be(ChannelType.EventConsume);
    }

    [Test]
    public void AddEventPublishChannel_RegistersChannel()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        
        // Act
        builder.AddEventPublishChannel("pub-channel", _ => {});
        
        // Assert
        var channel = builder.ChannelRegistry.GetPublishChannel("pub-channel");
        channel.Should().NotBeNull();
        channel!.Intent.Should().Be(ChannelType.EventPublish);
    }

    [Test]
    public void Consumes_RegistersMessageInChannel()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act
        builder.AddEventConsumeChannel("test-channel", c => c
            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>()));

        // Assert
        var channel = builder.ChannelRegistry.GetConsumeChannel("test-channel");
        channel.Should().NotBeNull();
        var msg = channel!.GetMessage(typeof(TestEvent));
        msg.Should().NotBeNull();
        msg!.MessageTypeName.Should().Be("test.event");
    }

    [Test]
    public void Produces_RegistersMessageInChannel()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        
        // Act
        builder.AddEventPublishChannel("pub-channel", c => c.Produces<TestEvent>());
        
        // Assert
        var channel = builder.ChannelRegistry.GetPublishChannel("pub-channel");
        channel.Should().NotBeNull();
        var msg = channel!.GetMessage(typeof(TestEvent));
        msg.Should().NotBeNull();
    }

    [Test]
    public void WithHandler_RegistersHandlerInDI()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act
        builder.AddEventConsumeChannel("test-channel", c => c
            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>()));
        var provider = services.BuildServiceProvider();

        // Assert - Can resolve as concrete type
        using (var scope = provider.CreateScope())
        {
            var concreteHandler = scope.ServiceProvider.GetService<TestEventHandler>();
            concreteHandler.Should().NotBeNull();
        }
    }
}
