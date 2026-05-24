using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.Tests.RabbitMq;

public class CloudEventsAmqpMapperOutgoingTests
{
    private readonly CloudEventsAmqpMapper _mapper = new(
        new CloudEventsOptions(),
        NullLogger<CloudEventsAmqpMapper>.Instance
    );

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
            Subject = "order-456",
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
            Time = now,
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
        var structuredMapper = new CloudEventsAmqpMapper(
            new CloudEventsOptions { ContentMode = CloudEventsContentMode.Structured },
            NullLogger<CloudEventsAmqpMapper>.Instance
        );
        var now = DateTimeOffset.UtcNow;
        var props = new MessageProperties
        {
            Id = "evt-123",
            Source = "/orders-service",
            Type = "order.created",
            Time = now,
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
        var structuredMapper = new CloudEventsAmqpMapper(
            new CloudEventsOptions { ContentMode = CloudEventsContentMode.Structured },
            NullLogger<CloudEventsAmqpMapper>.Instance
        );
        var props = new MessageProperties
        {
            Id = "evt-123",
            Source = "/test",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow,
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        structuredMapper.MapOutgoing(body, props, outgoing);

        // Assert
        outgoing.ContentType.Should().Be("application/cloudevents+json");
    }

    [Test]
    public void MapOutgoing_MissingId_Throws()
    {
        // Arrange
        var props = new MessageProperties
        {
            Source = "/test",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow,
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        var act = () => _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*id*required*");
    }

    [Test]
    public void MapOutgoing_MissingSource_Throws()
    {
        // Arrange
        var props = new MessageProperties
        {
            Id = "123",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow,
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        var act = () => _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*source*required*");
    }

    [Test]
    public void MapOutgoing_MissingType_Throws()
    {
        // Arrange
        var props = new MessageProperties
        {
            Id = "123",
            Source = "/test",
            Time = DateTimeOffset.UtcNow,
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        var act = () => _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*type*required*");
    }

    [Test]
    public void MapOutgoing_MissingTime_Throws()
    {
        // Arrange
        var props = new MessageProperties
        {
            Id = "123",
            Source = "/test",
            Type = "test.event",
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        var act = () => _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*time*required*");
    }

    [Test]
    public void MapOutgoing_BinaryMode_SetsCloudEventsTraceContextHeaders()
    {
        // Keep Ratatoskr trace continuity via CloudEvents-prefixed headers.
        // RabbitMQ.Client can independently manage bare traceparent/tracestate.
        var props = new MessageProperties
        {
            Id = "123",
            Source = "/test",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow,
            TraceParent = "00-traceid-spanid-01",
            TraceState = "vendor=value",
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        outgoing.Headers.Should().NotContainKey("traceparent");
        outgoing.Headers.Should().NotContainKey("tracestate");
        outgoing.Headers.Should().ContainKey("cloudEvents_traceparent");
        outgoing.Headers["cloudEvents_traceparent"].Should().Be("00-traceid-spanid-01");
        outgoing.Headers.Should().ContainKey("cloudEvents_tracestate");
        outgoing.Headers["cloudEvents_tracestate"].Should().Be("vendor=value");
    }

    [Test]
    public void MapOutgoing_StructuredMode_SetsTraceContextInEnvelopeExtensions()
    {
        var structuredMapper = new CloudEventsAmqpMapper(
            new CloudEventsOptions { ContentMode = CloudEventsContentMode.Structured },
            NullLogger<CloudEventsAmqpMapper>.Instance
        );
        var props = new MessageProperties
        {
            Id = "123",
            Source = "/test",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow,
            TraceParent = "00-struct-traceparent-01",
            TraceState = "struct=true",
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        var result = structuredMapper.MapOutgoing(body, props, outgoing);
        var envelope = JsonSerializer.Deserialize<CloudEventEnvelope>(result);

        envelope.Should().NotBeNull();
        envelope!.TryGetExtension<string>("traceparent", out var traceParent).Should().BeTrue();
        traceParent.Should().Be("00-struct-traceparent-01");
        envelope.TryGetExtension<string>("tracestate", out var traceState).Should().BeTrue();
        traceState.Should().Be("struct=true");
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
            Headers = { { "x-custom-header", "custom-value" } },
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
                { "x-normal-header", "should-be-included" },
            },
        };
        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        outgoing.Headers.Should().ContainKey("x-normal-header");
        // The cloudEvents-prefixed user headers should not appear as-is
        // (they would conflict with the protocol-level cloudEvents_ headers)
        var userCustomHeaders = outgoing
            .Headers.Keys.Where(k => k == "cloudEvents_custom" || k == "cloudEvents:custom")
            .ToList();
        userCustomHeaders.Should().BeEmpty();
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
            TransportMetadata = { { "retry-count", "1" }, { "original-exchange", "test-ex" } },
        };

        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        _mapper.MapOutgoing(body, props, outgoing);

        // Assert
        foreach (var (key, _) in outgoing.Headers)
        {
            key.Should().NotStartWith("x-ratatoskr-");
        }
    }

    [Test]
    public void MapStructuredMode_ShouldNotIncludeRatatoskrHeaders()
    {
        // Arrange
        var mapper = new CloudEventsAmqpMapper(
            new CloudEventsOptions { ContentMode = CloudEventsContentMode.Structured },
            NullLogger<CloudEventsAmqpMapper>.Instance
        );

        var props = new MessageProperties
        {
            Id = "123",
            Source = "/test",
            Type = "test.event",
            Time = DateTimeOffset.UtcNow,
            TransportMetadata = { { "retry-count", "1" }, { "original-exchange", "test-ex" } },
        };

        var outgoing = new BasicProperties();
        var body = Encoding.UTF8.GetBytes("{}");

        // Act
        mapper.MapOutgoing(body, props, outgoing);

        // Assert
        if (outgoing.Headers != null)
        {
            foreach (var (key, _) in outgoing.Headers)
            {
                key.Should().NotStartWith("x-ratatoskr-");
            }
        }
    }
}
