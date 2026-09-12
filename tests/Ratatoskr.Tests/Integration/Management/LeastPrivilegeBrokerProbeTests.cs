using System.Text;
using AwesomeAssertions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// Pins the broker behaviour the control-plane design is built on. These tests assert facts
/// about RabbitMQ under the least-privilege permission patterns, not about Ratatoskr code, so
/// that a broker upgrade which invalidates one of the design's premises fails here loudly
/// instead of silently breaking the management transport.
/// </summary>
[ClassDataSource<RestrictedRabbitMqFixture>(Shared = SharedType.PerTestSession)]
public class LeastPrivilegeBrokerProbeTests(RestrictedRabbitMqFixture broker)
{
    private const string Vhost = "/";

    [Test]
    public async Task Probe_PublishingToDefaultExchange_IsRefused()
    {
        await using var connection = await ConnectAsync(RestrictedRabbitMqFixture.ServiceIdentity);
        await using var channel = await ConfirmingChannelAsync(connection);

        // The default exchange is 'amq.default', which matches neither {user}\..* nor .*\.inbox$.
        var act = async () =>
            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: "orders.anything",
                mandatory: false,
                basicProperties: new BasicProperties(),
                body: Body("{}")
            );

        (await act.Should().ThrowAsync<OperationInterruptedException>())
            .Which.ShutdownReason!.ReplyCode.Should()
            .Be(403);
    }

    [Test]
    public async Task Probe_DeclaringServerNamedQueue_IsRefused()
    {
        await using var connection = await ConnectAsync(RestrictedRabbitMqFixture.ServiceIdentity);
        await using var channel = await connection.CreateChannelAsync();

        // amq.gen-* does not match the {user}\..* configure pattern.
        var act = async () =>
            await channel.QueueDeclareAsync(
                queue: string.Empty,
                durable: false,
                exclusive: true,
                autoDelete: true
            );

        (await act.Should().ThrowAsync<OperationInterruptedException>())
            .Which.ShutdownReason!.ReplyCode.Should()
            .Be(403);
    }

    [Test]
    public async Task Probe_DeclaringTransientNonExclusiveQueue_IsRefused()
    {
        await using var connection = await ConnectAsync(RestrictedRabbitMqFixture.ServiceIdentity);
        await using var channel = await connection.CreateChannelAsync();

        // RabbitMQ 4.1+ removed the transient_nonexcl_queues feature; 4.0 still allowed it,
        // which is exactly why the test image is pinned above 4.0.
        var act = async () =>
            await channel.QueueDeclareAsync(
                queue: "orders.transient.probe",
                durable: false,
                exclusive: false,
                autoDelete: false
            );

        await act.Should().ThrowAsync<OperationInterruptedException>();
    }

    [Test]
    public async Task Probe_PublishingToUndeclaredExchange_ClosesTheChannel()
    {
        await using var connection = await ConnectAsync(RestrictedRabbitMqFixture.ServiceIdentity);
        await using var channel = await ConfirmingChannelAsync(connection);

        var act = async () =>
            await channel.BasicPublishAsync(
                exchange: "dashboard.mgmt.discovery.inbox",
                routingKey: string.Empty,
                mandatory: false,
                basicProperties: new BasicProperties(),
                body: Body("{}")
            );

        (await act.Should().ThrowAsync<OperationInterruptedException>())
            .Which.ShutdownReason!.ReplyCode.Should()
            .Be(404);

        // The channel is unusable afterwards: heartbeat publishing must therefore own a
        // channel it can afford to lose rather than share one with command consumption.
        channel.IsOpen.Should().BeFalse();
    }

    [Test]
    public async Task Probe_SpoofedUserId_IsRefusedByTheBroker()
    {
        const string exchange = "orders.mgmt.probe-spoof.inbox";
        await using var owner = await ConnectAsync(RestrictedRabbitMqFixture.ServiceIdentity);
        await using var ownerChannel = await owner.CreateChannelAsync();
        await ownerChannel.ExchangeDeclareAsync(exchange, ExchangeType.Direct, durable: true);

        await using var attacker = await ConnectAsync(RestrictedRabbitMqFixture.AttackerIdentity);
        await using var attackerChannel = await ConfirmingChannelAsync(attacker);

        var act = async () =>
            await attackerChannel.BasicPublishAsync(
                exchange: exchange,
                routingKey: "svc.orders",
                mandatory: false,
                basicProperties: new BasicProperties
                {
                    UserId = RestrictedRabbitMqFixture.DashboardIdentity,
                },
                body: Body("{}")
            );

        (await act.Should().ThrowAsync<OperationInterruptedException>())
            .Which.ShutdownReason!.ReplyCode.Should()
            .Be(406);
    }

    [Test]
    public async Task Probe_AnonymousCommandFromAnyIdentity_IsDelivered()
    {
        // The broker is not an authentication boundary: write access to *.inbox is granted to
        // every identity in the vhost, so the agent has to authenticate its caller itself.
        var exchange = "orders.mgmt.probe-anon.inbox";
        var queue = "orders.mgmt.probe-anon.q";

        await using var owner = await ConnectAsync(RestrictedRabbitMqFixture.ServiceIdentity);
        await using var ownerChannel = await owner.CreateChannelAsync();
        await ownerChannel.ExchangeDeclareAsync(exchange, ExchangeType.Direct, durable: true);
        await ownerChannel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await ownerChannel.QueueBindAsync(queue, exchange, "svc.orders");

        await using var attacker = await ConnectAsync(RestrictedRabbitMqFixture.AttackerIdentity);
        await using var attackerChannel = await attacker.CreateChannelAsync();
        await attackerChannel.BasicPublishAsync(
            exchange: exchange,
            routingKey: "svc.orders",
            mandatory: false,
            basicProperties: new BasicProperties(),
            body: Body("""{"injected":true}""")
        );

        var delivered = await WaitForMessageAsync(ownerChannel, queue);
        delivered.Should().NotBeNull();
        delivered!.BasicProperties.UserId.Should().BeNull("an absent user_id must be treated as untrusted");
    }

    [Test]
    public async Task Probe_TruthfulUserId_IsReadableByTheReceiver()
    {
        var exchange = "orders.mgmt.probe-userid.inbox";
        var queue = "orders.mgmt.probe-userid.q";

        await using var owner = await ConnectAsync(RestrictedRabbitMqFixture.ServiceIdentity);
        await using var ownerChannel = await owner.CreateChannelAsync();
        await ownerChannel.ExchangeDeclareAsync(exchange, ExchangeType.Direct, durable: true);
        await ownerChannel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await ownerChannel.QueueBindAsync(queue, exchange, "svc.orders");

        await using var dashboard = await ConnectAsync(RestrictedRabbitMqFixture.DashboardIdentity);
        await using var dashboardChannel = await dashboard.CreateChannelAsync();
        await dashboardChannel.BasicPublishAsync(
            exchange: exchange,
            routingKey: "svc.orders",
            mandatory: false,
            basicProperties: new BasicProperties
            {
                UserId = RestrictedRabbitMqFixture.DashboardIdentity,
            },
            body: Body("{}")
        );

        var delivered = await WaitForMessageAsync(ownerChannel, queue);
        delivered.Should().NotBeNull();
        delivered!.BasicProperties.UserId.Should().Be(RestrictedRabbitMqFixture.DashboardIdentity);
    }

    [Test]
    public async Task Probe_ReceiverOwnedInboxExchange_AcceptsCrossIdentityPublish()
    {
        // This is the whole topology in miniature: the receiver declares {prefix}...inbox and
        // binds its own queue; the sender, a different identity, publishes into it.
        var exchange = "dashboard.mgmt.probe-cross.inbox";
        var queue = "dashboard.mgmt.probe-cross.r1";

        await using var dashboard = await ConnectAsync(RestrictedRabbitMqFixture.DashboardIdentity);
        await using var dashboardChannel = await dashboard.CreateChannelAsync();
        await dashboardChannel.ExchangeDeclareAsync(exchange, ExchangeType.Direct, durable: true);
        await dashboardChannel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true);
        await dashboardChannel.QueueBindAsync(queue, exchange, "r1");

        await using var service = await ConnectAsync(RestrictedRabbitMqFixture.ServiceIdentity);
        await using var serviceChannel = await service.CreateChannelAsync();
        await serviceChannel.BasicPublishAsync(
            exchange: exchange,
            routingKey: "r1",
            mandatory: false,
            basicProperties: new BasicProperties
            {
                UserId = RestrictedRabbitMqFixture.ServiceIdentity,
            },
            body: Body("""{"reply":true}""")
        );

        var delivered = await WaitForMessageAsync(dashboardChannel, queue);
        delivered.Should().NotBeNull();
    }

    [Test]
    public async Task Probe_FanoutDiscovery_ReachesEveryReplica()
    {
        var exchange = "dashboard.mgmt.probe-fanout.inbox";

        await using var dashboard = await ConnectAsync(RestrictedRabbitMqFixture.DashboardIdentity);
        await using var replicaOne = await dashboard.CreateChannelAsync();
        await using var replicaTwo = await dashboard.CreateChannelAsync();
        await replicaOne.ExchangeDeclareAsync(exchange, ExchangeType.Fanout, durable: true);

        await replicaOne.QueueDeclareAsync("dashboard.mgmt.probe-fanout.a", durable: false, exclusive: true, autoDelete: true);
        await replicaOne.QueueBindAsync("dashboard.mgmt.probe-fanout.a", exchange, string.Empty);
        await replicaTwo.QueueDeclareAsync("dashboard.mgmt.probe-fanout.b", durable: false, exclusive: true, autoDelete: true);
        await replicaTwo.QueueBindAsync("dashboard.mgmt.probe-fanout.b", exchange, string.Empty);

        await using var service = await ConnectAsync(RestrictedRabbitMqFixture.ServiceIdentity);
        await using var serviceChannel = await service.CreateChannelAsync();
        await serviceChannel.BasicPublishAsync(
            exchange: exchange,
            routingKey: string.Empty,
            mandatory: false,
            basicProperties: new BasicProperties(),
            body: Body("""{"heartbeat":true}""")
        );

        (await WaitForMessageAsync(replicaOne, "dashboard.mgmt.probe-fanout.a")).Should().NotBeNull();
        (await WaitForMessageAsync(replicaTwo, "dashboard.mgmt.probe-fanout.b")).Should().NotBeNull();
    }

    [Test]
    public async Task Probe_MandatoryPublishToUnboundRoutingKey_IsReturned()
    {
        var exchange = "orders.mgmt.probe-return.inbox";
        await using var owner = await ConnectAsync(RestrictedRabbitMqFixture.ServiceIdentity);
        await using var ownerChannel = await owner.CreateChannelAsync();
        await ownerChannel.ExchangeDeclareAsync(exchange, ExchangeType.Direct, durable: true);

        await using var dashboard = await ConnectAsync(RestrictedRabbitMqFixture.DashboardIdentity);
        await using var dashboardChannel = await dashboard.CreateChannelAsync();

        var returned = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        dashboardChannel.BasicReturnAsync += (_, args) =>
        {
            returned.TrySetResult(args.ReplyCode);
            return Task.CompletedTask;
        };

        await dashboardChannel.BasicPublishAsync(
            exchange: exchange,
            routingKey: "inst.dead-replica",
            mandatory: true,
            basicProperties: new BasicProperties(),
            body: Body("{}")
        );

        var replyCode = await returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        replyCode.Should().Be(312);
    }

    [Test]
    public async Task Probe_MandatoryPublishToBoundQueueWithoutConsumer_IsNotReturned()
    {
        var exchange = "orders.mgmt.probe-noreturn.inbox";
        var queue = "orders.mgmt.probe-noreturn.q";
        await using var owner = await ConnectAsync(RestrictedRabbitMqFixture.ServiceIdentity);
        await using var ownerChannel = await owner.CreateChannelAsync();
        await ownerChannel.ExchangeDeclareAsync(exchange, ExchangeType.Direct, durable: true);
        await ownerChannel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await ownerChannel.QueueBindAsync(queue, exchange, "svc.orders");

        await using var dashboard = await ConnectAsync(RestrictedRabbitMqFixture.DashboardIdentity);
        await using var dashboardChannel = await dashboard.CreateChannelAsync();

        var returned = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        dashboardChannel.BasicReturnAsync += (_, args) =>
        {
            returned.TrySetResult(args.ReplyCode);
            return Task.CompletedTask;
        };

        await dashboardChannel.BasicPublishAsync(
            exchange: exchange,
            routingKey: "svc.orders",
            mandatory: true,
            basicProperties: new BasicProperties(),
            body: Body("{}")
        );

        // A durable service queue with every replica temporarily down must not fail fast;
        // the command waits in the queue. Only a missing binding is unreachable.
        var raced = await Task.WhenAny(returned.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        raced.Should().NotBe(returned.Task);
    }

    private async Task<IConnection> ConnectAsync(string identity)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(broker.ConnectionStringFor(identity)),
            VirtualHost = Vhost,
        };
        return await factory.CreateConnectionAsync();
    }

    /// <summary>
    /// Opens a channel with publisher confirms. Without confirms RabbitMQ.Client 7 publishes
    /// fire-and-forget, so an access refusal or missing exchange surfaces later as an
    /// asynchronous channel shutdown rather than on the await, and a probe would pass vacuously.
    /// </summary>
    private static Task<IChannel> ConfirmingChannelAsync(IConnection connection) =>
        connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true
            )
        );

    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);

    private static async Task<BasicGetResult?> WaitForMessageAsync(IChannel channel, string queue)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: true);
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(50);
        }

        return null;
    }
}
