using System.Text;
using AwesomeAssertions;
using RabbitMQ.Client;
using Ratatoskr.CloudEvents;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.RabbitMq.Config;

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
            services.AddRatatoskr(bus => 
            {
                ConfigureBus(bus, QueueName);
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
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
            services.AddRatatoskr(bus => 
            {
                ConfigureBus(bus, QueueName);
                bus.AddHandler<TestEvent, TestEventHandler>(handler1)
                   .AddHandler<TestEvent, SecondTestEventHandler>(handler2);
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
            services.AddRatatoskr(bus => 
            {
                ConfigureBus(bus, QueueName);
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
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
            services.AddRatatoskr(bus =>
            {
                ConfigureBus(bus, QueueName);
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
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

    private void ConfigureBus(RatatoskrBuilder bus, string queueName)
    {
        bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
        bus.AddCommandConsumeChannel(queueName, c => c
            .WithRabbitMq(o => o.WithQueueName(queueName).WithAutoAck(false).WithTransientQueue()
                .WithQueueType(QueueType.Classic))
            .Consumes<TestEvent>());
    }

    private async Task PublishBinaryCloudEventAsync(string routingKey, TestEvent eventData)
    {
        var factory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var json = System.Text.Json.JsonSerializer.Serialize(eventData);
        var body = Encoding.UTF8.GetBytes(json);

        var props = new BasicProperties
        {
            ContentType = "application/json",
            Headers = new Dictionary<string, object?>
            {
                ["cloudEvents_specversion"] = "1.0",
                ["cloudEvents_type"] = "test.event",
                ["cloudEvents_source"] = "/test",
                ["cloudEvents_id"] = Guid.NewGuid().ToString()
            }
        };
        
        await channel.BasicPublishAsync(exchange: "", routingKey: routingKey, mandatory: false, basicProperties: props, body: body);
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
