using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.Tests.Fixtures;

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
        builder.AddEventConsumeChannel("test-channel", _ => { });

        // Assert
        var channel = builder.ChannelRegistry.GetConsumeChannel("test-channel");
        channel.Should().NotBeNull();
        channel.Intent.Should().Be(ChannelType.EventConsume);
    }

    [Test]
    public void AddEventPublishChannel_RegistersChannel()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act
        builder.AddEventPublishChannel("pub-channel", _ => { });

        // Assert
        var channel = builder.ChannelRegistry.GetPublishChannel("pub-channel");
        channel.Should().NotBeNull();
        channel.Intent.Should().Be(ChannelType.EventPublish);
    }

    [Test]
    public void Consumes_RegistersMessageInChannel()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act
        builder.AddEventConsumeChannel(
            "test-channel",
            c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>())
        );

        // Assert
        var channel = builder.ChannelRegistry.GetConsumeChannel("test-channel");
        channel.Should().NotBeNull();
        var msg = channel.GetMessage(typeof(TestEvent));
        msg.Should().NotBeNull();
        msg.MessageTypeName.Should().Be("test.event");
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
        var msg = channel.GetMessage(typeof(TestEvent));
        msg.Should().NotBeNull();
    }

    [Test]
    public void WithHandler_RegistersHandlerInDI()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act
        builder.AddEventConsumeChannel(
            "test-channel",
            c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>())
        );
        var provider = services.BuildServiceProvider();

        // Assert - Can resolve as concrete type
        using (var scope = provider.CreateScope())
        {
            var concreteHandler = scope.ServiceProvider.GetService<TestEventHandler>();
            concreteHandler.Should().NotBeNull();
        }
    }

    [Test]
    public void Produces_WithDataSchema_SetsDataSchemaOnRegistration()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act
        builder.AddEventPublishChannel(
            "pub-channel",
            c =>
                c.Produces<TestEvent>(m =>
                    m.WithDataSchema("https://schemas.example.com/test-event/v1.json")
                )
        );

        // Assert
        var channel = builder.ChannelRegistry.GetPublishChannel("pub-channel");
        var msg = channel!.GetMessage(typeof(TestEvent));
        msg!.DataSchema.Should().Be("https://schemas.example.com/test-event/v1.json");
    }

    [Test]
    public void Produces_WithDataSchemaAttribute_SetsDataSchemaFromAttribute()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act
        builder.AddEventPublishChannel("pub-channel", c => c.Produces<EventWithSchema>());

        // Assert
        var channel = builder.ChannelRegistry.GetPublishChannel("pub-channel");
        var msg = channel!.GetMessage(typeof(EventWithSchema));
        msg!.DataSchema.Should().Be("https://schemas.example.com/event-with-schema/v1.json");
    }

    [Test]
    public void Produces_WithDataSchemaBuilder_OverridesAttribute()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act
        builder.AddEventPublishChannel(
            "pub-channel",
            c =>
                c.Produces<EventWithSchema>(m =>
                    m.WithDataSchema("https://override.example.com/v2.json")
                )
        );

        // Assert
        var channel = builder.ChannelRegistry.GetPublishChannel("pub-channel");
        var msg = channel!.GetMessage(typeof(EventWithSchema));
        msg!.DataSchema.Should().Be("https://override.example.com/v2.json");
    }

    [Test]
    public void Produces_WithSerializer_SetsSerializerOnRegistration()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act
        builder.AddEventPublishChannel(
            "pub-channel",
            c => c.Produces<TestEvent>(m => m.WithSerializer<TestEventPipeMessageSerializer>())
        );

        // Assert
        var channel = builder.ChannelRegistry.GetPublishChannel("pub-channel");
        var msg = channel!.GetMessage(typeof(TestEvent));
        msg!.SerializerType.Should().Be(typeof(TestEventPipeMessageSerializer));
    }

    [Test]
    public void SerializerResolver_WithMixedDefaultAndExplicitSerializer_Throws()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<TestEventPipeMessageSerializer>();
        services.AddRatatoskr(bus =>
        {
            bus.AddEventPublishChannel("pub-a", c => c.Produces<TestEvent>());
            bus.AddEventConsumeChannel(
                "con-a",
                c =>
                    c.Consumes<TestEvent>(
                        h => h.WithHandler<TestEventHandler>(),
                        m => m.WithSerializer<TestEventPipeMessageSerializer>()
                    )
            );
        });
        using var provider = services.BuildServiceProvider();

        // Act
        var act = () => provider.GetRequiredService<IMessageSerializerResolver>();

        // Assert
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*mixes default and explicit serializer registrations*");
    }

    [Test]
    public void SerializerResolver_WithUnregisteredConcreteSerializer_ThrowsHelpfulMessage()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddRatatoskr(bus =>
        {
            bus.AddEventPublishChannel(
                "pub-a",
                c => c.Produces<TestEvent>(m => m.WithSerializer<TestEventPipeMessageSerializer>())
            );
        });
        using var provider = services.BuildServiceProvider();

        // Act
        var act = () => provider.GetRequiredService<IMessageSerializerResolver>();

        // Assert
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Register it as its concrete type*");
    }

    [RatatoskrMessage(
        "event.with.schema",
        DataSchema = "https://schemas.example.com/event-with-schema/v1.json"
    )]
    private record EventWithSchema;
}
