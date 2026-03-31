using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Core;

public class MessagePropertiesEnricherTests
{
    private readonly ChannelRegistry _registry = new();
    private readonly CloudEventsOptions _cloudEventsOptions = new() { DefaultSource = "/test-service" };
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly NoOpTransportEnricher _transportEnricher = new();

    private MessagePropertiesEnricher CreateEnricher() =>
        new(_registry, _cloudEventsOptions, _timeProvider, [_transportEnricher]);

    [Test]
    public void Enrich_WithRegisteredMessage_SetsTypeFromRegistry()
    {
        // Arrange
        var channel = new ChannelRegistration("test.exchange", ChannelType.EventPublish);
        channel.Messages.Add(new MessageRegistration(typeof(TestEvent), "test.event"));
        _registry.Register(channel);
        var enricher = CreateEnricher();

        // Act
        var result = enricher.Enrich<TestEvent>(null);

        // Assert
        result.Type.Should().Be("test.event");
    }

    [Test]
    public void Enrich_WithExplicitType_DoesNotOverwrite()
    {
        // Arrange
        var channel = new ChannelRegistration("test.exchange", ChannelType.EventPublish);
        channel.Messages.Add(new MessageRegistration(typeof(TestEvent), "test.event"));
        _registry.Register(channel);
        var enricher = CreateEnricher();
        var properties = new MessageProperties { Type = "custom.type" };

        // Act
        var result = enricher.Enrich<TestEvent>(properties);

        // Assert
        result.Type.Should().Be("custom.type");
    }

    [Test]
    public void Enrich_SetsSourceFromCloudEventsOptions()
    {
        // Arrange
        var enricher = CreateEnricher();

        // Act
        var result = enricher.Enrich<TestEvent>(null);

        // Assert
        result.Source.Should().Be("/test-service");
    }

    [Test]
    public void Enrich_WithExplicitSource_DoesNotOverwrite()
    {
        // Arrange
        var enricher = CreateEnricher();
        var properties = new MessageProperties { Source = "/custom-source" };

        // Act
        var result = enricher.Enrich<TestEvent>(properties);

        // Assert
        result.Source.Should().Be("/custom-source");
    }

    [Test]
    public void Enrich_GeneratesIdWhenMissing()
    {
        // Arrange
        var enricher = CreateEnricher();

        // Act
        var result = enricher.Enrich<TestEvent>(null);

        // Assert
        result.Id.Should().NotBeNullOrEmpty();
        Guid.TryParse(result.Id, out _).Should().BeTrue();
    }

    [Test]
    public void Enrich_WithExplicitId_DoesNotOverwrite()
    {
        // Arrange
        var enricher = CreateEnricher();
        var properties = new MessageProperties { Id = "my-custom-id" };

        // Act
        var result = enricher.Enrich<TestEvent>(properties);

        // Assert
        result.Id.Should().Be("my-custom-id");
    }

    [Test]
    public void Enrich_SetsTimestampFromTimeProvider()
    {
        // Arrange
        var now = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(now);
        var enricher = CreateEnricher();

        // Act
        var result = enricher.Enrich<TestEvent>(null);

        // Assert
        result.Time.Should().Be(now);
    }

    [Test]
    public void Enrich_WithNullProperties_CreatesNewProperties()
    {
        // Arrange
        var enricher = CreateEnricher();

        // Act
        var result = enricher.Enrich<TestEvent>(null);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().NotBeNullOrEmpty();
        result.Source.Should().NotBeNullOrEmpty();
        result.Time.Should().NotBeNull();
    }

    [Test]
    public void Enrich_UnregisteredMessageWithAttribute_SetsTypeFromAttribute()
    {
        // Arrange — TestEvent has [RatatoskrMessage("test.event")] but is NOT registered in the channel registry
        var enricher = CreateEnricher();

        // Act
        var result = enricher.Enrich<TestEvent>(null);

        // Assert
        result.Type.Should().Be("test.event");
    }

    [Test]
    public void Enrich_RegisteredMessage_CallsTransportEnricher()
    {
        // Arrange
        var channel = new ChannelRegistration("test.exchange", ChannelType.EventPublish);
        channel.Messages.Add(new MessageRegistration(typeof(TestEvent), "test.event"));
        channel.Transports.Add("test");
        _registry.Register(channel);
        var enricher = CreateEnricher();

        // Act
        enricher.Enrich<TestEvent>(null);

        // Assert
        _transportEnricher.WasCalled.Should().BeTrue();
    }

    [Test]
    public void Enrich_RegisteredMessage_WithNonMatchingTransport_DoesNotCallTransportEnricher()
    {
        // Arrange
        var channel = new ChannelRegistration("test.exchange", ChannelType.EventPublish);
        channel.Messages.Add(new MessageRegistration(typeof(TestEvent), "test.event"));
        channel.Transports.Add("other-transport");
        _registry.Register(channel);
        var enricher = CreateEnricher();

        // Act
        enricher.Enrich<TestEvent>(null);

        // Assert — enricher has TransportName "test", channel only has "other-transport"
        _transportEnricher.WasCalled.Should().BeFalse();
    }

    [Test]
    public void Enrich_RegisteredMessage_WithEmptyTransports_DoesNotCallTransportEnricher()
    {
        // Arrange — channel with no transports at all
        var channel = new ChannelRegistration("test.exchange", ChannelType.EventPublish);
        channel.Messages.Add(new MessageRegistration(typeof(TestEvent), "test.event"));
        _registry.Register(channel);
        var enricher = CreateEnricher();

        // Act
        enricher.Enrich<TestEvent>(null);

        // Assert
        _transportEnricher.WasCalled.Should().BeFalse();
    }

    [Test]
    public void Enrich_NonGeneric_BehavesSameAsGeneric()
    {
        // Arrange
        var channel = new ChannelRegistration("test.exchange", ChannelType.EventPublish);
        channel.Messages.Add(new MessageRegistration(typeof(TestEvent), "test.event"));
        _registry.Register(channel);
        var enricher = CreateEnricher();

        // Act
        var result = enricher.Enrich(typeof(TestEvent), null);

        // Assert
        result.Type.Should().Be("test.event");
        result.Source.Should().Be("/test-service");
        result.Id.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void Enrich_WithRegisteredDataSchema_SetsDataSchemaFromRegistry()
    {
        // Arrange
        var channel = new ChannelRegistration("test.exchange", ChannelType.EventPublish);
        var msgReg = new MessageRegistration(typeof(TestEvent), "test.event") { DataSchema = "https://schemas.example.com/v1.json" };
        channel.Messages.Add(msgReg);
        _registry.Register(channel);
        var enricher = CreateEnricher();

        // Act
        var result = enricher.Enrich<TestEvent>(null);

        // Assert
        result.DataSchema.Should().Be("https://schemas.example.com/v1.json");
    }

    [Test]
    public void Enrich_WithExplicitDataSchema_DoesNotOverwrite()
    {
        // Arrange
        var channel = new ChannelRegistration("test.exchange", ChannelType.EventPublish);
        var msgReg = new MessageRegistration(typeof(TestEvent), "test.event") { DataSchema = "https://schemas.example.com/v1.json" };
        channel.Messages.Add(msgReg);
        _registry.Register(channel);
        var enricher = CreateEnricher();
        var properties = new MessageProperties { DataSchema = "https://custom.example.com/v2.json" };

        // Act
        var result = enricher.Enrich<TestEvent>(properties);

        // Assert
        result.DataSchema.Should().Be("https://custom.example.com/v2.json");
    }

    [Test]
    public void Enrich_UnregisteredMessageWithDataSchemaAttribute_SetsDataSchemaFromAttribute()
    {
        // Arrange — EventWithDataSchema has DataSchema on [RatatoskrMessage] but is NOT registered in the registry
        var enricher = CreateEnricher();

        // Act
        var result = enricher.Enrich<EventWithDataSchema>(null);

        // Assert
        result.DataSchema.Should().Be("https://schemas.example.com/event-with-schema/v1.json");
    }

    [Test]
    public void Enrich_WithoutDataSchema_LeavesNull()
    {
        // Arrange
        var enricher = CreateEnricher();

        // Act
        var result = enricher.Enrich<TestEvent>(null);

        // Assert
        result.DataSchema.Should().BeNull();
    }

    [RatatoskrMessage("event.with.schema", DataSchema = "https://schemas.example.com/event-with-schema/v1.json")]
    private record EventWithDataSchema;

    private class NoOpTransportEnricher : ITransportMessageMetadataEnricher
    {
        public string TransportName => "test";
        public bool WasCalled { get; private set; }

        public void Enrich(PublishInformation publishInformation, MessageProperties properties)
        {
            WasCalled = true;
        }
    }
}
