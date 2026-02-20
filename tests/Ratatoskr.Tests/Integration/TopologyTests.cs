using AwesomeAssertions;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration;

public class TopologyTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string QueueName => $"topo-queue-{TestId}";

    [Test]
    public async Task Topology_QuorumQueue_CreatedCorrectly()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus => 
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddCommandConsumeChannel(QueueName, c => c
                    .WithRabbitMq(o => o
                        .WithQueueName(QueueName)
                        .WithQueueType(QueueType.Quorum))
                    .Consumes<TestEvent>());
            });
        });

        // Act & Assert
        // Verify it is NOT a classic queue by trying to declare it as classic (should fail)
        var factory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        // Expect 406 PRECONDITION_FAILED
        var act = async () => await channel.QueueDeclareAsync(
            queue: QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "classic" });

        await act.Should().ThrowAsync<OperationInterruptedException>()
            .Where(e => e.ShutdownReason.ReplyCode == 406);
    }

    [Test]
    public async Task Topology_DlqExchange_RoutingWorks()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus => 
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddCommandConsumeChannel(QueueName, c => c
                    .WithRabbitMq(o => o
                        .WithQueueName(QueueName)
                        .WithQueueType(QueueType.Quorum)
                        .WithRetry(1, TimeSpan.FromMilliseconds(100))) // Fast retry
                    .Consumes<TestEvent>());
                
                // Register a handler that always throws permanent error
                bus.AddHandler<TestEvent, PermanentErrorHandler>();
            });
        });

        // Act
        // Publish message
        await PublishToRabbitMqAsync(exchange: "", routingKey: QueueName, new TestEvent { Id = "dlq-test", Data = "fail" });

        // Assert
        // Message should end up in DLQ: {QueueName}.dlq
        var dlqName = $"{QueueName}.dlq";
        
        var factory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var messageFound = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            var result = await channel.BasicGetAsync(dlqName, true);
            if (result != null)
            {
                messageFound = true;
                break;
            }
            await Task.Delay(50);
        }

        messageFound.Should().BeTrue("Message should be routed to DLQ via DLQ Exchange");
    }



    public class PermanentErrorHandler : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties properties, CancellationToken cancellationToken)
        {
            throw new Exception("Force DLQ");
        }
    }
}
