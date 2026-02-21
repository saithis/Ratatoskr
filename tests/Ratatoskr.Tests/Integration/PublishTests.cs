using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration;

public class PublishTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string ExchangeName => $"pub-test-{TestId}";
    private string DefaultRoutingKey => "test.event";

    [Test]
    public async Task Publish_DirectToExchange_MessageDelivered()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus => 
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
            });
        });

        var queueName = $"pub-queue-{TestId}";
        await EnsureQueueBoundAsync(queueName, ExchangeName, DefaultRoutingKey);

        // Act
        var props = new MessageProperties();
        props.SetRoutingKey(DefaultRoutingKey);
        
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Data = "direct publish" }, props);
        });
        
        // Assert
        var message = await GetMessageAsync(queueName);
        message.Should().NotBeNull();
        
        var body = Encoding.UTF8.GetString(message.Body.ToArray());
        body.Should().Contain("direct publish");
    }

    [Test]
    public async Task Publish_WithBinaryContentMode_HeadersPresent()
    {
        // Arrange - Override configuration for this test
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus => 
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                        .WithRabbitMq(r => r.WithTopicExchange())
                        .Produces<TestEvent>())
                    .ConfigureCloudEvents(ce => ce.ContentMode = CloudEventsContentMode.Binary);
            });
        });
        
        var queueName = $"pub-binary-{TestId}";
        await EnsureQueueBoundAsync(queueName, ExchangeName, DefaultRoutingKey);

        // Act
        var props = new MessageProperties();
        props.SetRoutingKey(DefaultRoutingKey);

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Data = "binary mode" }, props);
        });

        // Assert
        var message = await GetMessageAsync(queueName);
        message.Should().NotBeNull();
        
        message!.BasicProperties.Headers.Should().NotBeNull();
        message.BasicProperties.Headers.Should().ContainKey("cloudEvents_specversion");
        message.BasicProperties.Headers.Should().ContainKey("cloudEvents_type");
    }

    [Test]
    public async Task Publish_WithStructuredContentMode_BodyStructureCorrect()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus => 
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                        .WithRabbitMq(r => r.WithTopicExchange())
                        .Produces<TestEvent>())
                    .ConfigureCloudEvents(ce => ce.ContentMode = CloudEventsContentMode.Structured);
            });
        });

        var queueName = $"pub-struct-{TestId}";
        await EnsureQueueBoundAsync(queueName, ExchangeName, DefaultRoutingKey);

        // Act
        var props = new MessageProperties();
        props.SetRoutingKey(DefaultRoutingKey);

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Data = "structured mode" }, props);
        });

        // Assert
        var message = await GetMessageAsync(queueName);
        message.Should().NotBeNull();

        var body = Encoding.UTF8.GetString(message!.Body.ToArray());
        body.Should().Contain("\"specversion\"");
        body.Should().Contain("\"data\"");
        body.Should().Contain("structured mode");
    }

    [Test]
    public async Task Publish_WithCustomRoutingKey_MessageRoutedCorrectly()
    {
        // Arrange - Configure a custom routing key on the message registration
        var customRoutingKey = "custom.routing.key";
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>(m => m.WithRoutingKey(customRoutingKey)));
            });
        });

        // Bind queue with the custom routing key (not the default "test.event")
        var queueName = $"pub-custom-rk-{TestId}";
        await EnsureQueueBoundAsync(queueName, ExchangeName, customRoutingKey);

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Data = "custom route" });
        });

        // Assert - Message should arrive via the custom routing key
        var message = await GetMessageAsync(queueName);
        message.Should().NotBeNull();

        var body = Encoding.UTF8.GetString(message!.Body.ToArray());
        body.Should().Contain("custom route");
    }

    [Test]
    public async Task Publish_MessageProperties_PreservedEndToEnd()
    {
        // Arrange
        var handler = new ContextCapturingHandler();
        var queueName = $"pub-props-{TestId}";

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddCommandConsumeChannel(queueName, c => c
                    .WithRabbitMq(o => o.WithQueueName(queueName).WithAutoAck(false).WithTransientQueue()
                        .WithQueueType(QueueType.Classic))
                    .Consumes<TestEvent>());
                bus.AddHandler<TestEvent, ContextCapturingHandler>(handler);
            });
        });

        // Act - Publish directly to queue with custom subject
        await PublishToRabbitMqWithSubjectAsync(queueName,
            new TestEvent { Data = "properties test" }, subject: "order-123");

        // Assert - Verify properties are preserved
        await WaitForConditionAsync(() => handler.CapturedContext != null, TimeSpan.FromSeconds(5));
        handler.CapturedContext.Should().NotBeNull();
        handler.CapturedContext!.Subject.Should().Be("order-123");
        handler.CapturedContext.Type.Should().Be("test.event");
    }

    private async Task PublishToRabbitMqWithSubjectAsync(string routingKey, TestEvent eventData, string subject)
    {
        var factory = new RabbitMQ.Client.ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var json = System.Text.Json.JsonSerializer.Serialize(eventData);
        var body = Encoding.UTF8.GetBytes(json);

        var props = new RabbitMQ.Client.BasicProperties
        {
            MessageId = Guid.NewGuid().ToString(),
            Type = "test.event",
            ContentType = "application/json",
            DeliveryMode = RabbitMQ.Client.DeliveryModes.Persistent,
            Headers = new Dictionary<string, object?>
            {
                ["cloudEvents_subject"] = subject
            }
        };

        await channel.BasicPublishAsync(exchange: "", routingKey: routingKey,
            mandatory: false, basicProperties: props, body: body);
    }
}
