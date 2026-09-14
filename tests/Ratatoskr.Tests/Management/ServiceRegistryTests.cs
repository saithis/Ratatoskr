using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Registry;

namespace Ratatoskr.Tests.Management;

public class ServiceRegistryTests
{
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly TimeSpan _staleAfter = TimeSpan.FromMinutes(2);

    private static ServiceAnnouncement CreateAnnouncement(
        string serviceName,
        string instanceId,
        DateTimeOffset announcedAt,
        params DbContextSummary[] dbContexts
    ) =>
        new()
        {
            ProtocolVersion = ManagementProtocol.Current,
            ServiceName = serviceName,
            InstanceId = instanceId,
            MachineName = "test-host",
            StartedAt = announcedAt.AddHours(-1),
            AnnouncedAt = announcedAt,
            DbContexts = dbContexts,
            Address = ManagementAddress.Empty,
        };

    [Test]
    public void ToCard_DeduplicatesDbContextBacklogAcrossMultipleLiveReplicas()
    {
        var registry = new ServiceRegistry(_timeProvider, _staleAfter);
        var now = _timeProvider.GetUtcNow();

        // 3 replicas of the same service reporting the same shared DbContext
        var replica1 = CreateAnnouncement(
            "orders",
            "inst-1",
            now.AddSeconds(-30),
            new DbContextSummary
            {
                Name = "OrdersDbContext",
                HasOutbox = true,
                HasInbox = true,
                PendingOutbox = 10,
                PoisonedOutbox = 1,
                PendingInbox = 5,
                PoisonedInbox = 0,
            }
        );

        var replica2 = CreateAnnouncement(
            "orders",
            "inst-2",
            now.AddSeconds(-15),
            new DbContextSummary
            {
                Name = "OrdersDbContext",
                HasOutbox = true,
                HasInbox = true,
                PendingOutbox = 12, // slightly newer count
                PoisonedOutbox = 1,
                PendingInbox = 4,
                PoisonedInbox = 0,
            }
        );

        var replica3 = CreateAnnouncement(
            "orders",
            "inst-3",
            now.AddSeconds(-5),
            new DbContextSummary
            {
                Name = "OrdersDbContext",
                HasOutbox = true,
                HasInbox = true,
                PendingOutbox = 15, // newest count
                PoisonedOutbox = 2,
                PendingInbox = 3,
                PoisonedInbox = 1,
            }
        );

        registry.Publish("amqp", replica1);
        registry.Publish("amqp", replica2);
        registry.Publish("amqp", replica3);

        var cards = registry.GetServices();
        cards.Should().HaveCount(1);

        var card = cards[0];
        card.ServiceName.Should().Be("orders");
        card.InstanceCount.Should().Be(3);
        card.OnlineInstanceCount.Should().Be(3);
        card.Liveness.Should().Be(ServiceLiveness.Online);

        // Crucial assertion: Backlog is NOT summed across replicas (10 + 12 + 15 = 37).
        // It takes the newest report per DbContext (15).
        card.PendingOutbox.Should().Be(15);
        card.PoisonedOutbox.Should().Be(2);
        card.PendingInbox.Should().Be(3);
        card.PoisonedInbox.Should().Be(1);
    }

    [Test]
    public void ToCard_SumsAcrossDistinctDbContexts_WhileDeduplicatingReplicas()
    {
        var registry = new ServiceRegistry(_timeProvider, _staleAfter);
        var now = _timeProvider.GetUtcNow();

        // 2 replicas, each reporting two distinct DbContexts (OrdersDbContext and PaymentsDbContext)
        var replica1 = CreateAnnouncement(
            "orders",
            "inst-1",
            now.AddSeconds(-20),
            new DbContextSummary
            {
                Name = "OrdersDbContext",
                PendingOutbox = 10,
                PoisonedOutbox = 0,
                PendingInbox = 5,
                PoisonedInbox = 0,
            },
            new DbContextSummary
            {
                Name = "PaymentsDbContext",
                PendingOutbox = 20,
                PoisonedOutbox = 2,
                PendingInbox = 8,
                PoisonedInbox = 1,
            }
        );

        var replica2 = CreateAnnouncement(
            "orders",
            "inst-2",
            now.AddSeconds(-10),
            new DbContextSummary
            {
                Name = "OrdersDbContext",
                PendingOutbox = 10,
                PoisonedOutbox = 0,
                PendingInbox = 5,
                PoisonedInbox = 0,
            },
            new DbContextSummary
            {
                Name = "PaymentsDbContext",
                PendingOutbox = 25, // newer
                PoisonedOutbox = 2,
                PendingInbox = 7,
                PoisonedInbox = 1,
            }
        );

        registry.Publish("amqp", replica1);
        registry.Publish("amqp", replica2);

        var cards = registry.GetServices();
        cards.Should().HaveCount(1);

        var card = cards[0];
        // Sum across distinct contexts: OrdersDbContext (10) + PaymentsDbContext (25) = 35
        card.PendingOutbox.Should().Be(35);
        card.PoisonedOutbox.Should().Be(2);
        card.PendingInbox.Should().Be(12); // 5 + 7
        card.PoisonedInbox.Should().Be(1); // 0 + 1
        card.ContextNames.Should().BeEquivalentTo(["OrdersDbContext", "PaymentsDbContext"]);
    }

    [Test]
    public void ToCard_PrefersOnlineReplicasOverStaleReplicas()
    {
        var registry = new ServiceRegistry(_timeProvider, _staleAfter);
        var now = _timeProvider.GetUtcNow();

        // Replica 1 is stale (announced 5 minutes ago, staleAfter is 2 minutes)
        var staleReplica = CreateAnnouncement(
            "orders",
            "inst-stale",
            now.AddMinutes(-5),
            new DbContextSummary
            {
                Name = "OrdersDbContext",
                PendingOutbox = 999, // stale large backlog
            }
        );

        // Replica 2 is online (announced 10 seconds ago)
        var onlineReplica = CreateAnnouncement(
            "orders",
            "inst-online",
            now.AddSeconds(-10),
            new DbContextSummary
            {
                Name = "OrdersDbContext",
                PendingOutbox = 4, // current backlog
            }
        );

        registry.Publish("amqp", staleReplica);
        registry.Publish("amqp", onlineReplica);

        var cards = registry.GetServices();
        cards.Should().HaveCount(1);

        var card = cards[0];
        card.InstanceCount.Should().Be(2);
        card.OnlineInstanceCount.Should().Be(1);
        // Only online replica's report is used
        card.PendingOutbox.Should().Be(4);
    }
}
