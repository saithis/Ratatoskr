using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.Tests.RabbitMq;

public class CloudEventsAmqpMapperRoundTripTests
{
    private readonly CloudEventsAmqpMapper _mapper = new(new CloudEventsOptions(), NullLogger<CloudEventsAmqpMapper>.Instance);

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

        // Assert — round-trip preserves key properties.
        // TraceParent is NOT asserted here: in production, RabbitMQ.Client 7.x sets the
        // traceparent AMQP header during BasicPublishAsync, which the mapper does not replicate.
        result.props.Id.Should().Be("roundtrip-id");
        result.props.Source.Should().Be("/roundtrip-source");
        result.props.Type.Should().Be("roundtrip.type");
        result.props.Subject.Should().Be("roundtrip-subject");
        result.body.Should().BeEquivalentTo(originalBody);
    }

    [Test]
    public void MapOutgoing_StructuredMode_RoundTripsCorrectly()
    {
        // Arrange
        var structuredMapper = new CloudEventsAmqpMapper(
            new CloudEventsOptions { ContentMode = CloudEventsContentMode.Structured },
            NullLogger<CloudEventsAmqpMapper>.Instance);
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

        // Assert — TraceParent/TraceState are NOT asserted here: in production,
        // RabbitMQ.Client 7.x sets them during BasicPublishAsync.
        result.props.Id.Should().Be("struct-roundtrip-id");
        result.props.Source.Should().Be("/struct-roundtrip");
        result.props.Type.Should().Be("struct.roundtrip");
    }

    [Test]
    public void MapOutgoing_BinaryMode_DataSchema_RoundTripsCorrectly()
    {
        // Arrange
        var originalProps = new MessageProperties
        {
            Id = "ds-test-id",
            Source = "/ds-source",
            Type = "ds.type",
            Time = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero),
            DataSchema = "https://schemas.example.com/events/ds.type/v1.json",
        };
        var outgoing = new BasicProperties();
        var originalBody = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");

        // Act — outgoing then incoming
        var mappedBody = _mapper.MapOutgoing(originalBody, originalProps, outgoing);
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", outgoing, mappedBody);
        var result = _mapper.MapIncoming(incoming);

        // Assert
        result.props.DataSchema.Should().Be("https://schemas.example.com/events/ds.type/v1.json");
    }

    [Test]
    public void MapOutgoing_StructuredMode_DataSchema_RoundTripsCorrectly()
    {
        // Arrange
        var structuredMapper = new CloudEventsAmqpMapper(
            new CloudEventsOptions { ContentMode = CloudEventsContentMode.Structured },
            NullLogger<CloudEventsAmqpMapper>.Instance);
        var originalProps = new MessageProperties
        {
            Id = "ds-struct-id",
            Source = "/ds-struct",
            Type = "ds.struct.type",
            Time = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero),
            DataSchema = "https://schemas.example.com/events/ds.struct.type/v1.json",
        };
        var outgoing = new BasicProperties();
        var originalBody = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");

        // Act
        var mappedBody = structuredMapper.MapOutgoing(originalBody, originalProps, outgoing);
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", outgoing, mappedBody);
        var result = structuredMapper.MapIncoming(incoming);

        // Assert
        result.props.DataSchema.Should().Be("https://schemas.example.com/events/ds.struct.type/v1.json");
    }

    [Test]
    public void MapOutgoing_BinaryMode_CustomCloudEventsExtension_RoundTripsIntoIncomingPropertiesDictionary()
    {
        const string extKey = "ratatoskrplayground_scenariorun";
        const string extVal = "00000000-0000-4000-8000-000000000042";
        var now = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var originalProps = new MessageProperties
        {
            Id = "ext-roundtrip-id",
            Source = "/ext-source",
            Type = "ext.roundtrip",
            Time = now,
            CloudEventExtensions = { [extKey] = extVal },
        };
        var outgoing = new BasicProperties();
        var originalBody = Encoding.UTF8.GetBytes("{\"ok\":true}");

        var mappedBody = _mapper.MapOutgoing(originalBody, originalProps, outgoing);
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", outgoing, mappedBody);
        var result = _mapper.MapIncoming(incoming);

        result.props.CloudEventExtensions.Should().ContainKey(extKey);
        result.props.CloudEventExtensions[extKey].Should().Be(extVal);
    }

    [Test]
    public void MapOutgoing_BinaryMode_NullDataSchema_OmitsHeader()
    {
        // Arrange
        var originalProps = new MessageProperties
        {
            Id = "no-ds-id",
            Source = "/no-ds",
            Type = "no.ds.type",
            Time = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero),
        };
        var outgoing = new BasicProperties();
        var originalBody = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");

        // Act
        _mapper.MapOutgoing(originalBody, originalProps, outgoing);

        // Assert — no dataschema header should be present
        outgoing.Headers.Should().NotContainKey("cloudEvents_dataschema");
    }
}
