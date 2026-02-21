using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.Tests.RabbitMq;

public class CloudEventsAmqpMapperTests
{
    private readonly CloudEventsAmqpMapper _mapper = new(new CloudEventsOptions());

    [Test]
    public void MapBinaryModeIncoming_ShouldMapTraceContext()
    {
        // Arrange
        var headers = new Dictionary<string, object?>
        {
            { "traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01" },
            { "tracestate", "rojo=00f067aa0ba902b7" },
            { "cloudEvents_id", "123" },
            { "cloudEvents_source", "/unit-test" },
            { "cloudEvents_type", "test.event" }
        };
        
        var basicProperties = new BasicProperties 
        { 
            Headers = headers,
            ContentType = "application/json"
        };
        
        var body = Encoding.UTF8.GetBytes("{}");
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", basicProperties, body);

        // Act
        var result = _mapper.MapIncoming(incoming);

        // Assert
        result.props.TraceParent.Should().Be("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        result.props.TraceState.Should().Be("rojo=00f067aa0ba902b7");
    }

    [Test]
    public void MapStructuredModeIncoming_ShouldMapTraceContext()
    {
        // Arrange
        var cloudEventJson = "{\"id\":\"123\",\"source\":\"/unit-test\",\"type\":\"test.event\",\"specversion\":\"1.0\",\"data\":{\"foo\":\"bar\"}}";
        var body = Encoding.UTF8.GetBytes(cloudEventJson);
        
        var headers = new Dictionary<string, object?>
        {
            { "traceparent", "00-structured-trace-id-01" },
            { "tracestate", "structured=true" }
        };

        var basicProperties = new BasicProperties 
        { 
            Headers = headers,
            ContentType = "application/cloudevents+json"
        };
        
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", basicProperties, body);

        // Act
        var result = _mapper.MapIncoming(incoming);

        // Assert
        result.props.TraceParent.Should().Be("00-structured-trace-id-01");
        result.props.TraceState.Should().Be("structured=true");
    }

    [Test]
    public void MapBinaryMode_ShouldNotIncludeRatatoskrHeaders()
    {
        // Arrange
        var props = new MessageProperties
        {
            Id = "123",
            Source = "/test",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow,
            TransportMetadata = 
            {
                { "retry-count", "1" },
                { "original-exchange", "test-ex" }
            }
        };
        
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        foreach (var (key, value) in outgoing.Headers)
        {
            key.Should().NotStartWith("x-ratatoskr-");
        }
    }

    [Test]
    public void MapStructuredMode_ShouldNotIncludeRatatoskrHeaders()
    {
        // Arrange
        var mapper = new CloudEventsAmqpMapper(new CloudEventsOptions { ContentMode = CloudEventsContentMode.Structured });

        var props = new MessageProperties
        {
            Id = "123",
            Source = "/test",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow,
            TransportMetadata =
            {
                { "retry-count", "1" },
                { "original-exchange", "test-ex" }
            }
        };

        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        mapper.MapOutgoing(body, props, outgoing);

        // Assert
        if (outgoing.Headers != null)
        {
            foreach (var (key, value) in outgoing.Headers)
            {
                key.Should().NotStartWith("x-ratatoskr-");
            }
        }
    }

    [Test]
    public void MapOutgoing_BinaryMode_SetsAllCloudEventHeaders()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var props = new MessageProperties
        {
            Id = "evt-123",
            Source = "/orders-service",
            Type = "order.created",
            Time = now,
            Subject = "order-456"
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{\"orderId\":\"456\"}");

        // Act
        _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        outgoing.Headers.Should().ContainKey("cloudEvents_specversion");
        outgoing.Headers["cloudEvents_specversion"].Should().Be("1.0");
        outgoing.Headers["cloudEvents_id"].Should().Be("evt-123");
        outgoing.Headers["cloudEvents_type"].Should().Be("order.created");
        outgoing.Headers["cloudEvents_source"].Should().Be("/orders-service");
        outgoing.Headers.Should().ContainKey("cloudEvents_time");
        outgoing.Headers.Should().ContainKey("cloudEvents_subject");
        outgoing.Headers["cloudEvents_subject"].Should().Be("order-456");
    }

    [Test]
    public void MapOutgoing_BinaryMode_SetsStandardRabbitMqProperties()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var props = new MessageProperties
        {
            Id = "evt-123",
            Source = "/orders-service",
            Type = "order.created",
            Time = now
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        outgoing.ContentType.Should().Be("application/json");
        outgoing.DeliveryMode.Should().Be(DeliveryModes.Persistent);
        outgoing.MessageId.Should().Be("evt-123");
        outgoing.Type.Should().Be("order.created");
        outgoing.AppId.Should().Be("/orders-service");
    }

    [Test]
    public void MapOutgoing_StructuredMode_BodyContainsCloudEventEnvelope()
    {
        // Arrange
        var structuredMapper = new CloudEventsAmqpMapper(new CloudEventsOptions { ContentMode = CloudEventsContentMode.Structured });
        var now = DateTimeOffset.UtcNow;
        var props = new MessageProperties
        {
            Id = "evt-123",
            Source = "/orders-service",
            Type = "order.created",
            Time = now
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{\"orderId\":\"456\"}");

        // Act
        var result = structuredMapper.MapOutgoing(body, props, outgoing);

        // Assert
        var envelope = JsonSerializer.Deserialize<CloudEventEnvelope>(result);
        envelope.Should().NotBeNull();
        envelope!.Id.Should().Be("evt-123");
        envelope.Source.Should().Be("/orders-service");
        envelope.Type.Should().Be("order.created");
        envelope.SpecVersion.Should().Be("1.0");
        envelope.Data.Should().NotBeNull();
    }

    [Test]
    public void MapOutgoing_StructuredMode_ContentTypeIsCloudEventsJson()
    {
        // Arrange
        var structuredMapper = new CloudEventsAmqpMapper(new CloudEventsOptions { ContentMode = CloudEventsContentMode.Structured });
        var props = new MessageProperties
        {
            Id = "evt-123",
            Source = "/test",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        structuredMapper.MapOutgoing(body, props, outgoing);

        // Assert
        outgoing.ContentType.Should().Be("application/cloudevents+json");
    }

    [Test]
    public void MapIncoming_BinaryMode_ExtractsAllProperties()
    {
        // Arrange
        var headers = new Dictionary<string, object?>
        {
            { "cloudEvents_specversion", "1.0" },
            { "cloudEvents_id", "evt-789" },
            { "cloudEvents_source", "/billing" },
            { "cloudEvents_type", "payment.received" },
            { "cloudEvents_time", "2025-06-15T12:00:00Z" },
            { "cloudEvents_subject", "payment-001" },
            { "cloudEvents_datacontenttype", "application/json" }
        };
        var basicProperties = new BasicProperties
        {
            Headers = headers,
            ContentType = "application/json",
            MessageId = "evt-789",
            Type = "payment.received",
            AppId = "/billing"
        };
        var body = Encoding.UTF8.GetBytes("{\"amount\":100}");
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", basicProperties, body);

        // Act
        var result = _mapper.MapIncoming(incoming);

        // Assert
        result.props.Id.Should().Be("evt-789");
        result.props.Source.Should().Be("/billing");
        result.props.Type.Should().Be("payment.received");
        result.props.Subject.Should().Be("payment-001");
        result.props.Time.Should().NotBeNull();
        result.body.Should().BeEquivalentTo(body);
    }

    [Test]
    public void MapIncoming_StructuredMode_ExtractsDataFromEnvelope()
    {
        // Arrange
        var cloudEventJson = JsonSerializer.Serialize(new
        {
            id = "evt-100",
            source = "/inventory",
            type = "item.updated",
            specversion = "1.0",
            time = "2025-06-15T12:00:00Z",
            data = new { itemId = "abc", quantity = 5 }
        });
        var body = Encoding.UTF8.GetBytes(cloudEventJson);
        var basicProperties = new BasicProperties
        {
            ContentType = "application/cloudevents+json"
        };
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", basicProperties, body);

        // Act
        var result = _mapper.MapIncoming(incoming);

        // Assert
        result.props.Id.Should().Be("evt-100");
        result.props.Source.Should().Be("/inventory");
        result.props.Type.Should().Be("item.updated");
        result.body.Should().NotBeEmpty();
        var data = JsonSerializer.Deserialize<JsonElement>(result.body);
        data.GetProperty("itemId").GetString().Should().Be("abc");
        data.GetProperty("quantity").GetInt32().Should().Be(5);
    }

    [Test]
    public void MapOutgoing_MissingId_Throws()
    {
        // Arrange
        var props = new MessageProperties
        {
            Source = "/test",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        var act = () => _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*id*required*");
    }

    [Test]
    public void MapOutgoing_MissingSource_Throws()
    {
        // Arrange
        var props = new MessageProperties
        {
            Id = "123",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        var act = () => _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*source*required*");
    }

    [Test]
    public void MapOutgoing_MissingType_Throws()
    {
        // Arrange
        var props = new MessageProperties
        {
            Id = "123",
            Source = "/test",
            Time = DateTimeOffset.UtcNow
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        var act = () => _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*type*required*");
    }

    [Test]
    public void MapOutgoing_MissingTime_Throws()
    {
        // Arrange
        var props = new MessageProperties
        {
            Id = "123",
            Source = "/test",
            Type = "test.event"
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        var act = () => _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*time*required*");
    }

    [Test]
    public void MapOutgoing_BinaryMode_TraceContextIncludedInHeaders()
    {
        // Arrange
        var props = new MessageProperties
        {
            Id = "123",
            Source = "/test",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow,
            TraceParent = "00-traceid-spanid-01",
            TraceState = "vendor=value"
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        outgoing.Headers.Should().ContainKey("traceparent");
        outgoing.Headers["traceparent"].Should().Be("00-traceid-spanid-01");
        outgoing.Headers.Should().ContainKey("tracestate");
        outgoing.Headers["tracestate"].Should().Be("vendor=value");
    }

    [Test]
    public void MapOutgoing_BinaryMode_RoundTripsCorrectly()
    {
        // Arrange
        var now = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var originalProps = new MessageProperties
        {
            Id = "roundtrip-id",
            Source = "/roundtrip-source",
            Type = "roundtrip.type",
            Time = now,
            Subject = "roundtrip-subject",
            TraceParent = "00-roundtrip-trace-01",
            TraceState = "roundtrip=true"
        };
        var outgoing = new BasicProperties();
        var originalBody = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");

        // Act — outgoing
        var mappedBody = _mapper.MapOutgoing(originalBody, originalProps, outgoing);

        // Act — incoming (simulate receiving the message)
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", outgoing, mappedBody);
        var result = _mapper.MapIncoming(incoming);

        // Assert — round-trip preserves key properties
        result.props.Id.Should().Be("roundtrip-id");
        result.props.Source.Should().Be("/roundtrip-source");
        result.props.Type.Should().Be("roundtrip.type");
        result.props.Subject.Should().Be("roundtrip-subject");
        result.props.TraceParent.Should().Be("00-roundtrip-trace-01");
        result.body.Should().BeEquivalentTo(originalBody);
    }

    [Test]
    public void MapOutgoing_StructuredMode_RoundTripsCorrectly()
    {
        // Arrange
        var structuredMapper = new CloudEventsAmqpMapper(new CloudEventsOptions { ContentMode = CloudEventsContentMode.Structured });
        var now = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var originalProps = new MessageProperties
        {
            Id = "struct-roundtrip-id",
            Source = "/struct-roundtrip",
            Type = "struct.roundtrip",
            Time = now,
            TraceParent = "00-struct-trace-01",
            TraceState = "struct=true"
        };
        var outgoing = new BasicProperties();
        var originalBody = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");

        // Act — outgoing
        var mappedBody = structuredMapper.MapOutgoing(originalBody, originalProps, outgoing);

        // Act — incoming
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", outgoing, mappedBody);
        var result = structuredMapper.MapIncoming(incoming);

        // Assert
        result.props.Id.Should().Be("struct-roundtrip-id");
        result.props.Source.Should().Be("/struct-roundtrip");
        result.props.Type.Should().Be("struct.roundtrip");
        result.props.TraceParent.Should().Be("00-struct-trace-01");
        result.props.TraceState.Should().Be("struct=true");
    }

    [Test]
    public void MapOutgoing_BinaryMode_CustomHeadersPreserved()
    {
        // Arrange
        var props = new MessageProperties
        {
            Id = "123",
            Source = "/test",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow,
            Headers = { { "x-custom-header", "custom-value" } }
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        outgoing.Headers.Should().ContainKey("x-custom-header");
        outgoing.Headers["x-custom-header"].Should().Be("custom-value");
    }

    [Test]
    public void MapOutgoing_BinaryMode_CloudEventsPrefixedHeadersFiltered()
    {
        // Arrange
        var props = new MessageProperties
        {
            Id = "123",
            Source = "/test",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow,
            Headers =
            {
                { "cloudEvents_custom", "should-be-filtered" },
                { "cloudEvents:custom", "should-also-be-filtered" },
                { "x-normal-header", "should-be-included" }
            }
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        outgoing.Headers.Should().ContainKey("x-normal-header");
        // The cloudEvents-prefixed user headers should not appear as-is
        // (they would conflict with the protocol-level cloudEvents_ headers)
        var userCustomHeaders = outgoing.Headers.Keys
            .Where(k => k == "cloudEvents_custom" || k == "cloudEvents:custom")
            .ToList();
        userCustomHeaders.Should().BeEmpty();
    }
}
