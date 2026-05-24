using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration;

public class RetryTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string QueueName => $"retry-queue-{TestId}";
    private string RetryQueue => $"{QueueName}.retry";
    private string DlqName => $"{QueueName}.dlq";

    [Test]
    public async Task Consume_HandlerThrows_MovesToRetryQueue()
    {
        // Arrange
        await StartTestAsync(services => ConfigureRetryConsumer(services, maxRetries: 3));

        // Act
        await PublishToRabbitMqAsync(
            exchange: "",
            routingKey: QueueName,
            new TestEvent { Id = "retry-1", Data = "fail" }
        );

        // Assert - Message processed (invoked) and moved to retry queue
        await WaitForConditionAsync(
            async () => await GetMessageCountAsync(RetryQueue) > 0,
            TimeSpan.FromSeconds(5),
            "Message did not move to retry queue"
        );
    }

    [Test]
    public async Task Consume_MaxRetriesExceeded_MovesToDlq()
    {
        // Arrange
        await StartTestAsync(services => ConfigureRetryConsumer(services, maxRetries: 2));

        // Act
        await PublishToRabbitMqAsync(
            exchange: "",
            routingKey: QueueName,
            new TestEvent { Id = "dlq-1", Data = "fail" }
        );

        // Assert - Wait for Retries + DLQ move
        await WaitForConditionAsync(
            async () => await GetMessageCountAsync(DlqName) == 1,
            TimeSpan.FromSeconds(5),
            "Message did not move to DLQ"
        );

        var mainQueueCount = await GetMessageCountAsync(QueueName);
        mainQueueCount.Should().Be(0);
    }

    [Test]
    public async Task Consume_UnknownEventType_MovesToDlq()
    {
        // Arrange - Handler registered for TestEvent, but we'll send an unknown event type
        await StartTestAsync(services => ConfigureRetryConsumer(services, maxRetries: 2));

        // Act - Send unknown event type
        await PublishToRabbitMqAsync(
            exchange: "",
            routingKey: QueueName,
            new TestEvent { Id = "nohandler", Data = "fail" },
            type: "unknown.event"
        );

        // Assert - Should go to DLQ immediately (Permanent Error: No Handler)
        await WaitForConditionAsync(
            async () => await GetMessageCountAsync(DlqName) == 1,
            TimeSpan.FromSeconds(5),
            "Message did not move to DLQ"
        );
    }

    private void ConfigureRetryConsumer(IServiceCollection services, int maxRetries)
    {
        services.AddRatatoskr(bus =>
        {
            bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
            bus.AddCommandConsumeChannel(
                QueueName,
                c =>
                    c.WithRabbitMq(o =>
                            o.WithQueueName(QueueName)
                                .WithAutoAck(false)
                                .WithRetry(r =>
                                    r.WithMaxRetries(maxRetries)
                                        .WithDelay(TimeSpan.FromMilliseconds(50))
                                )
                                .WithTransientQueue()
                                .WithQueueType(QueueType.Classic)
                        )
                        .Consumes<TestEvent>(m => m.WithHandler<ThrowingTestEventHandler>())
            );
        });
    }
}
