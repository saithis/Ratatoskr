using System.Text;
using AwesomeAssertions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.Tests.RabbitMq;

public class CloudEventsAmqpMapperRoundTripTests
{
    private readonly CloudEventsAmqpMapper _mapper = new(new CloudEventsOptions());

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
}
