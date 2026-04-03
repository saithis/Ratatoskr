using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Ratatoskr.CloudEvents;
using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration;

public class ConsumeTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string QueueName => $"cons-queue-{TestId}";

    [Test]
    public async Task Consume_DirectPublish_HandlerInvoked()
    {
        // Arrange
        var handler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddSingleton<TestEventHandler>(handler);
            services.AddRatatoskr(bus =>
            {
                ConfigureBus(bus, QueueName, c => c.WithHandler<TestEventHandler>());
            });
        });

        // Act
        await PublishToRabbitMqAsync(exchange: "", routingKey: QueueName, new TestEvent { Id = "123", Data = "consumed" });

        // Assert
        await WaitForConditionAsync(() => handler.HandledMessages.Count > 0, TimeSpan.FromSeconds(2));
        handler.HandledMessages.Should().HaveCount(1);
        handler.HandledMessages[0].Id.Should().Be("123");
    }

    [Test]
    public async Task Consume_MultipleHandlers_AllInvoked()
    {
        // Arrange
        var handler1 = new TestEventHandler();
        var handler2 = new SecondTestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddSingleton<TestEventHandler>(handler1);
            services.AddSingleton<SecondTestEventHandler>(handler2);
            services.AddRatatoskr(bus =>
            {
                ConfigureBus(bus, QueueName, c => c
                    .WithHandler<TestEventHandler>()
                    .WithHandler<SecondTestEventHandler>());
            });
        });

        // Act
        await PublishToRabbitMqAsync(exchange: "", routingKey: QueueName, new TestEvent { Id = "multi", Data = "cast" });

        // Assert
        await WaitForConditionAsync(() => handler1.HandledMessages.Count > 0 && handler2.HandledMessages.Count > 0, TimeSpan.FromSeconds(2));
        handler1.HandledMessages.Should().HaveCount(1);
        handler2.HandledMessages.Should().HaveCount(1);
    }

    [Test]
    public async Task Consume_BinaryCloudEvent_DeserializedCorrectly()
    {
        // Arrange
        var handler = new TestEventHandler();
        await StartTestAsync(services =>
        {
            services.AddSingleton<TestEventHandler>(handler);
            services.AddRatatoskr(bus =>
            {
                ConfigureBus(bus, QueueName, c => c.WithHandler<TestEventHandler>());
                bus.ConfigureCloudEvents(ce => ce.ContentMode = CloudEventsContentMode.Binary);
            });
        });

        // Act
        await PublishBinaryCloudEventAsync(QueueName, new TestEvent { Id = "bin-1", Data = "binary data" });

        // Assert
        await WaitForConditionAsync(() => handler.HandledMessages.Count > 0, TimeSpan.FromSeconds(2));
        handler.HandledMessages.Should().HaveCount(1);
        handler.HandledMessages[0].Id.Should().Be("bin-1");
    }

    [Test]
    public async Task Consume_StructuredCloudEvent_DeserializedCorrectly()
    {
        // Arrange
        var handler = new TestEventHandler();
        await StartTestAsync(services =>
        {
            services.AddSingleton<TestEventHandler>(handler);
            services.AddRatatoskr(bus =>
            {
                ConfigureBus(bus, QueueName, c => c.WithHandler<TestEventHandler>());
                bus.ConfigureCloudEvents(ce => ce.ContentMode = CloudEventsContentMode.Structured);
            });
        });

        // Act
        await PublishStructuredCloudEventAsync(QueueName, new TestEvent { Id = "struct-1", Data = "structured data" });

        // Assert
        await WaitForConditionAsync(() => handler.HandledMessages.Count > 0, TimeSpan.FromSeconds(2));
        handler.HandledMessages.Should().HaveCount(1);
        handler.HandledMessages[0].Id.Should().Be("struct-1");
    }

    [Test]
    public async Task Consume_WithPerMessageSerializer_UsesConfiguredSerializer()
    {
        // Arrange
        var handler = new TestEventHandler();
        await StartTestAsync(services =>
        {
            services.AddSingleton<TestEventHandler>(handler);
            services.AddSingleton<TestEventPipeMessageSerializer>();
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddCommandConsumeChannel(QueueName, c =>
                {
                    c.WithRabbitMq(o => o.WithQueueName(QueueName).WithAutoAck(false).WithTransientQueue()
                        .WithQueueType(QueueType.Classic));
                    c.Consumes<TestEvent>(h => h.WithHandler<TestEventHandler>(),
                        m => m.WithSerializer<TestEventPipeMessageSerializer>());
                });
            });
        });

        var serializer = new TestEventPipeMessageSerializer();
        var serializedBody = serializer.Serialize(new TestEvent { Id = "pipe-1", Data = "pipe-data" });

        // Act
        await PublishBinaryCloudEventRawAsync(
            QueueName,
            serializedBody,
            contentType: serializer.ContentType,
            type: "test.event");

        // Assert
        await WaitForConditionAsync(() => handler.HandledMessages.Count > 0, TimeSpan.FromSeconds(2));
        handler.HandledMessages.Should().HaveCount(1);
        handler.HandledMessages[0].Id.Should().Be("pipe-1");
        handler.HandledMessages[0].Data.Should().Be("pipe-data");
    }

    [Test]
    public async Task Consume_UnknownEventType_HandlerNotInvoked()
    {
        // Arrange
        var handler = new TestEventHandler();
        var dlqName = $"{QueueName}.dlq";

        await StartTestAsync(services =>
        {
            services.AddSingleton<TestEventHandler>(handler);
            services.AddRatatoskr(bus =>
            {
                ConfigureBusWithRetry(bus, QueueName, maxRetries: 1, c => c.WithHandler<TestEventHandler>());
            });
        });

        // Act - Send a message with an unknown event type
        await PublishToRabbitMqAsync(exchange: "", routingKey: QueueName,
            new TestEvent { Id = "unknown", Data = "test" }, type: "unknown.event.type");

        // Assert - Handler should not be invoked for unknown event types
        await WaitForConditionAsync(async () => await GetMessageCountAsync(dlqName) > 0,
            TimeSpan.FromSeconds(5), "Unknown event message did not move to DLQ");
        handler.HandledMessages.Should().BeEmpty();
    }

    [Test]
    public async Task Consume_MessageExceedsSizeLimit_MessageRejectedAndSentToDlq()
    {
        // Arrange
        var handler = new TestEventHandler();
        var dlqName = $"{QueueName}.dlq";

        await StartTestAsync(services =>
        {
            services.AddSingleton<TestEventHandler>(handler);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o =>
                {
                    o.ConnectionString = new Uri(RabbitMqConnectionString);
                    o.MaxInboundMessageSize = 10; // Very small limit
                });

                bus.AddCommandConsumeChannel(QueueName, c =>
                {
                    c.WithRabbitMq(o => o
                        .WithQueueName(QueueName)
                        .WithAutoAck(false)
                        .WithRetry(r => r.WithMaxRetries(1).WithDelay(TimeSpan.FromMilliseconds(50)))
                        .WithTransientQueue()
                        .WithQueueType(QueueType.Classic));
                    c.Consumes<TestEvent>(h => h.WithHandler<TestEventHandler>());
                });
            });
        });

        // Act - Send a message with size > 10 bytes
        await PublishToRabbitMqAsync(exchange: "", routingKey: QueueName,
            new TestEvent { Id = "large", Data = "this is a large message" });

        // Assert - Handler should not be invoked, message goes to DLQ
        await WaitForConditionAsync(async () => await GetMessageCountAsync(dlqName) > 0,
            TimeSpan.FromSeconds(5), "Oversized message did not move to DLQ");
        handler.HandledMessages.Should().BeEmpty();
    }

    [Test]
    public async Task Consume_HandlerThrows_MessageNotAcked()
    {
        // Arrange
        var handler = new ThrowingTestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddSingleton<ThrowingTestEventHandler>(handler);
            services.AddRatatoskr(bus =>
            {
                ConfigureBusWithRetry(bus, QueueName, maxRetries: 3, c => c.WithHandler<ThrowingTestEventHandler>());
            });
        });

        // Act
        await PublishToRabbitMqAsync(exchange: "", routingKey: QueueName,
            new TestEvent { Id = "throw-1", Data = "will throw" });

        // Assert - Handler is invoked multiple times (message redelivered after nack)
        // This proves the message was NOT acked on the first failure
        await WaitForConditionAsync(() => handler.ReceivedMessages.Count >= 2, TimeSpan.FromSeconds(5),
            "Message was not redelivered after handler failure");
        handler.ReceivedMessages.Should().AllSatisfy(e => e.Id.Should().Be("throw-1"));
    }

    [Test]
    public async Task Consume_WithInboxHandler_ViaRabbitMq_MessageAcceptedWithRabbitMqTransportName()
    {
        // Verifies that when a message arrives from RabbitMQ and is dispatched to an inbox handler,
        // the inbox message is stored with TransportName = "rabbitmq" (Issue 6 fix).
        // Background processing is disabled so we can inspect the DB before the handler runs.

        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                ConfigureBus(bus, QueueName, c => c
                    .WithHandler<NoOpTestEventHandler>("consume-noop"),
                    inbox => inbox.WithoutBackgroundProcessing());
            });
            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            await db.Database.EnsureCreatedAsync();
        });

        // Act — publish to RabbitMQ, consumer receives and accepts to inbox
        await PublishToRabbitMqAsync(exchange: "", routingKey: QueueName,
            new TestEvent { Id = "inbox-rmq-1", Data = "inbox via rmq" });

        // Assert — message appears in inbox DB with RabbitMQ transport name
        // Note: InboxMessageEntity.Id is MessageProperties.Id (from BasicProperties.MessageId),
        // NOT TestEvent.Id. We query for any inbox message instead.
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                return await db.Set<InboxMessageEntity>().AnyAsync();
            }),
            TimeSpan.FromSeconds(10));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var inboxMessage = await db.Set<InboxMessageEntity>().SingleAsync();
            inboxMessage.TransportName.Should().Be("rabbitmq");

            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.HandlerKey.Should().Be("consume-noop");
            status.CompletedAt.Should().BeNull("background processing is disabled");
        });
    }

    private void ConfigureBus(RatatoskrBuilder bus, string queueName,
        Action<MessageConsumptionBuilder<TestEvent>> configureHandler,
        Action<InboxBuilder<TestDbContext>>? configureInbox = null)
    {
        bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
        bus.AddCommandConsumeChannel(queueName, c =>
        {
            c.WithRabbitMq(o => o.WithQueueName(queueName).WithAutoAck(false).WithTransientQueue()
                .WithQueueType(QueueType.Classic));
            var channel = c.Consumes<TestEvent>(configureHandler);
            if (configureInbox != null)
                channel.UseInbox<TestDbContext>();
        });
        if (configureInbox != null)
            bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(configureInbox));
    }

    private void ConfigureBusWithRetry(RatatoskrBuilder bus, string queueName, int maxRetries,
        Action<MessageConsumptionBuilder<TestEvent>> configureHandler)
    {
        bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
        bus.AddCommandConsumeChannel(queueName, c =>
        {
            c.WithRabbitMq(o => o
                .WithQueueName(queueName)
                .WithAutoAck(false)
                .WithRetry(r => r.WithMaxRetries(maxRetries).WithDelay(TimeSpan.FromMilliseconds(50)))
                .WithTransientQueue()
                .WithQueueType(QueueType.Classic));
            c.Consumes<TestEvent>(configureHandler);
        });
    }

    private async Task PublishBinaryCloudEventAsync(string routingKey, TestEvent eventData)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(eventData);
        var body = Encoding.UTF8.GetBytes(json);
        await PublishBinaryCloudEventRawAsync(
            routingKey,
            body,
            contentType: "application/json",
            type: "test.event");
    }

    private async Task PublishBinaryCloudEventRawAsync(string routingKey, byte[] body, string contentType, string type)
    {
        var factory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var props = CreateBinaryCloudEventProperties(contentType, type);

        await channel.BasicPublishAsync(exchange: "", routingKey: routingKey, mandatory: false, basicProperties: props, body: body);
    }

    private static BasicProperties CreateBinaryCloudEventProperties(string contentType, string type)
    {
        return new BasicProperties
        {
            ContentType = contentType,
            Headers = new Dictionary<string, object?>
            {
                ["cloudEvents_specversion"] = "1.0",
                ["cloudEvents_type"] = type,
                ["cloudEvents_source"] = "/test",
                ["cloudEvents_id"] = Guid.NewGuid().ToString()
            }
        };
    }

    private async Task PublishStructuredCloudEventAsync(string routingKey, TestEvent eventData)
    {
        var factory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var cloudEvent = new
        {
            specversion = "1.0",
            type = "test.event",
            source = "/test",
            id = Guid.NewGuid().ToString(),
            datacontenttype = "application/json",
            data = eventData
        };

        var json = System.Text.Json.JsonSerializer.Serialize(cloudEvent);
        var body = Encoding.UTF8.GetBytes(json);

        var props = new BasicProperties
        {
            ContentType = "application/cloudevents+json"
        };
        
        await channel.BasicPublishAsync(exchange: "", routingKey: routingKey, mandatory: false, basicProperties: props, body: body);
    }

}
