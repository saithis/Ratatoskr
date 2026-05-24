using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Testing;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.MessageTracking;

public class MessageTrackingTransportTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : MessageTrackingTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Tracking_TransportShape_RawBodyAssertable()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
            });
            services.AddRatatoskrTesting();
        });

        var queueName = $"track-shape-{TestId}";
        await EnsureQueueBoundAsync(queueName, ExchangeName, DefaultRoutingKey);

        await using var session = Services.CreateTrackingSession();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            var props = new MessageProperties();
            props.SetRoutingKey(DefaultRoutingKey);
            await bus.PublishDirectAsync(
                new TestEvent { Id = "shape-1", Data = "transport shape" },
                props
            );
        });

        // Assert - verify the raw bytes on the wire
        var sent = await session.WaitForSentAsync<TestEvent>(TimeSpan.FromSeconds(5));
        var rawJson = Encoding.UTF8.GetString(sent.RawBody!);
        rawJson.Should().Contain("shape-1");
        rawJson.Should().Contain("transport shape");

        // Verify properties reflect CloudEvents metadata
        sent.Properties.Type.Should().Be("test.event");
        sent.Properties.Id.Should().NotBeNullOrEmpty();
        sent.Properties.Source.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task MessageCollection_ShouldHaveNoMessage_ThrowsWhenPresent()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
            });
            services.AddRatatoskrTesting();
        });

        await using var session = Services.CreateTrackingSession();

        // Initially no messages
        session.Published.ShouldHaveNoMessage<TestEvent>();

        // Publish
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            var props = new MessageProperties();
            props.SetRoutingKey(DefaultRoutingKey);
            await bus.PublishDirectAsync(
                new TestEvent { Id = "neg-1", Data = "negative test" },
                props
            );
        });

        var published = await session.WaitForPublishedAsync<TestEvent>(TimeSpan.FromSeconds(5));
        published.GetMessage<TestEvent>().Id.Should().Be("neg-1");

        // Now it should throw
        var act = session.Published.ShouldHaveNoMessage<TestEvent>;
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public async Task Tracking_Sent_TransportMessage_HasWireHeaders()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
            });
            services.AddRatatoskrTesting();
        });

        var queueName = $"track-transport-sent-{TestId}";
        await EnsureQueueBoundAsync(queueName, ExchangeName, DefaultRoutingKey);

        await using var session = Services.CreateTrackingSession();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            var props = new MessageProperties();
            props.SetRoutingKey(DefaultRoutingKey);
            await bus.PublishDirectAsync(
                new TestEvent { Id = "wire-1", Data = "wire format" },
                props
            );
        });

        // Assert - Sent stage should have TransportMessage with wire-level headers
        var sent = await session.WaitForSentAsync<TestEvent>(TimeSpan.FromSeconds(5));
        sent.TransportMessage.Should().NotBeNull();

        var headers = sent.TransportMessage.Headers;
        headers["content-type"].Should().Be("application/json");
        headers["message-id"].Should().Be(sent.Properties.Id);
        headers["type"].Should().Be("test.event");
        headers["delivery-mode"].Should().Be(2); // Persistent
        headers["cloudEvents_specversion"].Should().Be("1.0");
        headers["cloudEvents_type"].Should().Be("test.event");

        // Metadata should contain routing information
        var metadata = sent.TransportMessage.Metadata;
        metadata["exchange"].Should().Be(ExchangeName);
        metadata["routing-key"].Should().Be(DefaultRoutingKey);

        // Body should be the wire body
        sent.TransportMessage.Body.Should().NotBeEmpty();
        Encoding.UTF8.GetString(sent.TransportMessage.Body).Should().Contain("wire-1");

        // Published stage should NOT have TransportMessage
        var published = session.Published.Single<TestEvent>();
        published.TransportMessage.Should().BeNull();
    }

    [Test]
    public async Task Tracking_Received_TransportMessage_HasRawWireData()
    {
        // Arrange
        var handler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddSingleton(handler);
            services.AddRatatoskr(bus =>
            {
                ConfigureConsumeBus(bus, m => m.WithHandler<TestEventHandler>());
            });
            services.AddRatatoskrTesting();
        });

        await using var session = Services.CreateTrackingSession();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "wire-recv-1", Data = "receive wire" }
            );
        });

        // Assert - Received stage should have TransportMessage with raw wire data
        var received = await session.WaitForReceivedAsync<TestEvent>(TimeSpan.FromSeconds(5));
        received.TransportMessage.Should().NotBeNull();

        var headers = received.TransportMessage.Headers;
        headers.Should().ContainKey("content-type");
        headers.Should().ContainKey("message-id");
        headers.Should().ContainKey("type");

        // Delivery metadata from RabbitMQ
        var metadata = received.TransportMessage.Metadata;
        metadata.Should().ContainKey("exchange");
        metadata.Should().ContainKey("routing-key");
        metadata.Should().ContainKey("redelivered");
        metadata["redelivered"].Should().Be(false);

        // Body should be the raw wire bytes
        received.TransportMessage.Body.Should().NotBeEmpty();
        Encoding.UTF8.GetString(received.TransportMessage.Body).Should().Contain("wire-recv-1");

        // Dispatched stage should NOT have TransportMessage
        var dispatched = await session.WaitForDispatchedAsync<TestEvent>(TimeSpan.FromSeconds(5));
        dispatched.TransportMessage.Should().BeNull();
    }

    [Test]
    public async Task Tracking_InboxQueued_CapturesInboxQueuedStage()
    {
        // Arrange: EF Core transport + inbox, tracking session captures InboxQueued
        await StartTestAsync(services =>
        {
            var channelName = $"track-inbox-events-{TestId}";
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(channelName, c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel(
                    channelName,
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<NoOpTestEventHandler>("no-op"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });
            services.AddRatatoskrTesting();
            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        await using var session = Services.CreateTrackingSession();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "inbox-queue-1", Data = "track inbox" }
            );
        });

        // Assert — InboxQueued stage emitted by InboxAcceptor
        var queued = await session.WaitForInboxQueuedAsync<TestEvent>(TimeSpan.FromSeconds(10));
        queued.Properties.Id.Should().NotBeNullOrEmpty();
        queued.TransportName.Should().Be("efcore");
    }

    [Test]
    public async Task Tracking_InboxDispatched_CapturesInboxDispatchedStage()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            var channelName = $"track-inbox-disp-{TestId}";
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(channelName, c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel(
                    channelName,
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<NoOpTestEventHandler>("no-op"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });
            services.AddRatatoskrTesting();
            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        await using var session = Services.CreateTrackingSession();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "inbox-disp-1", Data = "track dispatch" }
            );
        });

        // Assert — InboxDispatched stage emitted after handler completes
        var dispatched = await session.WaitForInboxDispatchedAsync<TestEvent>(
            TimeSpan.FromSeconds(15)
        );
        dispatched.Properties.Id.Should().NotBeNullOrEmpty();

        // Both stages should be captured for the same message
        session.InboxQueued.ShouldHaveMessage<TestEvent>();
        session.InboxDispatched.ShouldHaveMessage<TestEvent>();
    }
}
