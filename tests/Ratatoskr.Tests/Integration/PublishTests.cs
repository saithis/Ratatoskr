using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;
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
}
