using System.Text.Json;
using AwesomeAssertions;
using Ratatoskr.Management.Contracts;
using TUnit.Core;

namespace Ratatoskr.Tests.Management;

public class ManagementProtocolSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void RequestEnvelope_HasStableJsonSnapshot()
    {
        var request = new ManagementRequestEnvelope
        {
            ProtocolVersion = new ProtocolVersion(1, 0), RequestId = "request-001", OperationId = "operation-001",
            Target = new ManagementTarget("orders", "orders-1", "orders-db"), Operation = "outbox.requeue",
            Deadline = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero),
            Actor = new ManagementActor("operator-42", "Ada", "Bearer", "trace-001"),
            Payload = new ManagementPayload("ratatoskr.management.outbox.requeue.v1", "{\"id\":\"message-001\"}"),
        };

        JsonSerializer.Serialize(request, JsonOptions).Should().Be("""
            {"protocolVersion":{"major":1,"minor":0},"requestId":"request-001","operationId":"operation-001","target":{"logicalServiceName":"orders","instanceId":"orders-1","resourceId":"orders-db"},"operation":"outbox.requeue","deadline":"2026-09-11T12:00:00+00:00","actor":{"subject":"operator-42","displayName":"Ada","authenticationType":"Bearer","correlationId":"trace-001"},"payload":{"type":"ratatoskr.management.outbox.requeue.v1","json":"{\u0022id\u0022:\u0022message-001\u0022}","contentType":"application/json"}}
            """);
    }

    [Test]
    public void ServiceAnnouncementAndTopology_HaveStableJsonSnapshot()
    {
        var announcement = new ServiceInstanceAnnouncement
        {
            ProtocolVersion = new ProtocolVersion(1, 0), LogicalServiceName = "orders", InstanceId = "orders-1",
            StartedAt = new DateTimeOffset(2026, 9, 11, 11, 0, 0, TimeSpan.Zero), AnnouncedAt = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero),
            Capabilities = [new CapabilityDescriptor("inbox", new ProtocolVersion(1, 0))],
        };
        var topology = new ChannelTopology("orders.events", ChannelIntent.Publish, ["OrderCreated"],
            [new TransportBinding("kafka", "orders-events", new SortedDictionary<string, string>(StringComparer.Ordinal) { ["topic"] = "orders-events" })]);

        JsonSerializer.Serialize(new { announcement, topology }, JsonOptions).Should().Be("""
            {"announcement":{"protocolVersion":{"major":1,"minor":0},"logicalServiceName":"orders","instanceId":"orders-1","startedAt":"2026-09-11T11:00:00+00:00","announcedAt":"2026-09-11T12:00:00+00:00","capabilities":[{"name":"inbox","version":{"major":1,"minor":0}}]},"topology":{"logicalName":"orders.events","intent":0,"messageTypes":["OrderCreated"],"transportBindings":[{"providerKind":"kafka","displayName":"orders-events","properties":{"topic":"orders-events"}}]}}
            """);
    }

    [Test]
    public void UnsupportedOperation_UsesStableStructuredError()
    {
        var request = CreateRequest();
        var response = ManagementResponseEnvelope.Failed(request, new ManagementError(ManagementProtocol.UnsupportedOperation, "The operation is not supported."));

        response.Success.Should().BeFalse();
        response.Error.Should().Be(new ManagementError(ManagementProtocol.UnsupportedOperation, "The operation is not supported."));
    }

    private static ManagementRequestEnvelope CreateRequest() => new()
    {
        ProtocolVersion = ManagementProtocol.Current, RequestId = "request-001", OperationId = "operation-001",
        Target = new ManagementTarget("orders"), Operation = "unknown", Deadline = DateTimeOffset.UtcNow,
    };
}
