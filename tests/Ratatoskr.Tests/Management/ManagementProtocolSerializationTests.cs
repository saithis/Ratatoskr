using System.Text.Json;
using AwesomeAssertions;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Tests.Management;

/// <summary>
/// Pins the wire format. Everything asserted here crosses a broker between independently
/// deployed processes, so a change that looks like a harmless rename is a breaking protocol
/// change and has to be seen as one.
/// </summary>
public class ManagementProtocolSerializationTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 11, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OperationId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Test]
    public void RequestEnvelope_HasStableJsonSnapshot()
    {
        var request = new ManagementRequestEnvelope
        {
            ProtocolVersion = new ProtocolVersion(1, 0),
            RequestId = "request-001",
            OperationId = OperationId,
            Target = new ManagementTarget("orders", "orders-1", "OrdersDbContext"),
            Operation = ManagementOperationNames.OutboxRequeue,
            Deadline = Now,
            Actor = new ManagementActor("operator-42", "Ada", "Bearer", "trace-001"),
            Payload = ManagementJson.ToElement(
                new MutateByIdsRequest { Ids = [Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff")] }
            ),
            ReplyTo = "dashboard.mgmt.reply.inbox|r1",
        };

        Serialize(request)
            .Should()
            .Be(
                """{"protocolVersion":{"major":1,"minor":0},"requestId":"request-001","operationId":"11111111-2222-3333-4444-555555555555","target":{"serviceName":"orders","instanceId":"orders-1","resource":"OrdersDbContext"},"operation":"outbox.requeue","deadline":"2026-09-11T12:00:00+00:00","actor":{"subject":"operator-42","displayName":"Ada","authenticationType":"Bearer","correlationId":"trace-001"},"payload":{"ids":["6f9619ff-8b86-d011-b42d-00cf4fc964ff"]},"replyTo":"dashboard.mgmt.reply.inbox|r1"}"""
            );
    }

    [Test]
    public void RequestEnvelope_OmitsAbsentOptionalFields()
    {
        var request = new ManagementRequestEnvelope
        {
            ProtocolVersion = new ProtocolVersion(1, 0),
            RequestId = "request-002",
            OperationId = OperationId,
            Target = new ManagementTarget("orders"),
            Operation = ManagementOperationNames.ServiceDescribe,
            Deadline = Now,
        };

        Serialize(request)
            .Should()
            .Be(
                """{"protocolVersion":{"major":1,"minor":0},"requestId":"request-002","operationId":"11111111-2222-3333-4444-555555555555","target":{"serviceName":"orders"},"operation":"service.describe","deadline":"2026-09-11T12:00:00+00:00"}"""
            );
    }

    [Test]
    public void ResponseEnvelope_SuccessHasStableJsonSnapshot()
    {
        var response = ManagementResponseEnvelope.Ok(
            Request(),
            ManagementJson.ToElement(new MessageCountResponse(17))
        );

        Serialize(response)
            .Should()
            .Be(
                """{"protocolVersion":{"major":1,"minor":0},"requestId":"request-001","operationId":"11111111-2222-3333-4444-555555555555","status":"Ok","payload":{"count":17}}"""
            );
    }

    [Test]
    public void ResponseEnvelope_FailureCarriesTheStableCode()
    {
        var response = ManagementResponseEnvelope.Failed(
            Request(),
            ManagementResultStatus.Unsupported,
            new ManagementError(
                ManagementErrorCodes.UnsupportedOperation,
                "The operation is not supported."
            )
        );

        Serialize(response)
            .Should()
            .Be(
                """{"protocolVersion":{"major":1,"minor":0},"requestId":"request-001","operationId":"11111111-2222-3333-4444-555555555555","status":"Unsupported","error":{"code":"unsupported_operation","detail":"The operation is not supported.","isRetryable":false}}"""
            );
    }

    [Test]
    public void ServiceAnnouncement_HasStableJsonSnapshot()
    {
        var announcement = new ServiceAnnouncement
        {
            ProtocolVersion = new ProtocolVersion(1, 0),
            ServiceName = "orders",
            InstanceId = "orders-1",
            MachineName = "pod-7",
            Environment = "Production",
            StartedAt = Started,
            AnnouncedAt = Now,
            Capabilities =
            [
                new CapabilityDescriptor(
                    ManagementCapabilityNames.Outbox,
                    new ProtocolVersion(1, 0)
                ),
            ],
            DbContexts =
            [
                new DbContextSummary
                {
                    Name = "OrdersDbContext",
                    HasOutbox = true,
                    HasInbox = false,
                    PendingOutbox = 3,
                    PoisonedOutbox = 1,
                },
            ],
            Channels =
            [
                new ChannelTopology(
                    "orders.events",
                    ChannelIntent.Publish,
                    ["OrderCreated"],
                    [
                        new TransportBinding(
                            "rabbitmq",
                            "orders-events",
                            new SortedDictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["exchange"] = "orders.events",
                            }
                        ),
                    ]
                ),
            ],
            Address = ManagementAddress.From(
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exchange"] = "orders.mgmt.cmd.inbox",
                    ["instanceKey"] = "inst.orders-1",
                    ["serviceKey"] = "svc.orders",
                }
            ),
        };

        Serialize(announcement)
            .Should()
            .Be(
                """{"protocolVersion":{"major":1,"minor":0},"serviceName":"orders","instanceId":"orders-1","machineName":"pod-7","environment":"Production","startedAt":"2026-09-11T11:00:00+00:00","announcedAt":"2026-09-11T12:00:00+00:00","capabilities":[{"name":"outbox","version":{"major":1,"minor":0}}],"dbContexts":[{"name":"OrdersDbContext","hasOutbox":true,"hasInbox":false,"pendingOutbox":3,"poisonedOutbox":1,"pendingInbox":0,"poisonedInbox":0}],"channels":[{"logicalName":"orders.events","intent":"Publish","messageTypes":["OrderCreated"],"transportBindings":[{"providerKind":"rabbitmq","displayName":"orders-events","properties":{"exchange":"orders.events"}}],"queues":[]}],"address":{"values":{"exchange":"orders.mgmt.cmd.inbox","instanceKey":"inst.orders-1","serviceKey":"svc.orders"}}}"""
            );
    }

    [Test]
    public void ServiceAnnouncement_RoundTripsThroughJson()
    {
        var announcement = new ServiceAnnouncement
        {
            ProtocolVersion = ManagementProtocol.Current,
            ServiceName = "orders",
            InstanceId = "orders-1",
            MachineName = "pod-7",
            StartedAt = Started,
            AnnouncedAt = Now,
            Address = ManagementAddress.From(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["exchange"] = "x" }
            ),
        };

        var roundTripped = JsonSerializer.Deserialize<ServiceAnnouncement>(
            Serialize(announcement),
            ManagementJson.Options
        );

        roundTripped.Should().BeEquivalentTo(announcement);
        roundTripped!.Address.Get("exchange").Should().Be("x");
    }

    [Test]
    public void MessageFilter_SerialisesStatusAsAName()
    {
        // An ordinal would silently change meaning the day someone inserts a status in the
        // middle of the enum, and a filter that changes meaning deletes the wrong rows.
        var filter = new MessageFilter
        {
            Status = MessageStatusFilter.Pending,
            From = Started,
            Search = "order-42",
        };

        Serialize(filter)
            .Should()
            .Be("""{"status":"Pending","from":"2026-09-11T11:00:00+00:00","search":"order-42"}""");
    }

    [Test]
    public void ProtocolVersion_AcceptsSameMajorAndLowerMinor()
    {
        new ProtocolVersion(1, 0).IsCompatibleWith(new ProtocolVersion(1, 2)).Should().BeTrue();
        new ProtocolVersion(1, 2).IsCompatibleWith(new ProtocolVersion(1, 2)).Should().BeTrue();
        new ProtocolVersion(1, 3).IsCompatibleWith(new ProtocolVersion(1, 2)).Should().BeFalse();
        new ProtocolVersion(2, 0).IsCompatibleWith(new ProtocolVersion(1, 2)).Should().BeFalse();
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, ManagementJson.Options);

    private static ManagementRequestEnvelope Request() =>
        new()
        {
            ProtocolVersion = ManagementProtocol.Current,
            RequestId = "request-001",
            OperationId = OperationId,
            Target = new ManagementTarget("orders"),
            Operation = ManagementOperationNames.OutboxCount,
            Deadline = Now,
        };
}
