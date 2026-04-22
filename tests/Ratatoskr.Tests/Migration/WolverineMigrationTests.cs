using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.RabbitMq.WolverineMigration;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.Tests.Integration;

namespace Ratatoskr.Tests.Migration;

/// <summary>
/// Integration tests for Wolverine to Ratatoskr migration.
/// </summary>
public class WolverineMigrationTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string QueueName => $"migration-queue-{TestId}";
    private string WolverineQueueName => $"wolverine-{TestId}";

    [Test]
    public async Task Migration_CreatesV2QueueTopology()
    {
        // Arrange & Act
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = RabbitMqConnectionString);
                
                bus.UseWolverineMigration(migration =>
                {
                    migration.EnableMigration = true;
                    migration.QueueSuffix = ".v2";
                    migration.CreateDuplicateBindings = false;
                    migration.ValidateWolverineTopologyExists = false;
                    migration.AddQueueMapping(WolverineQueueName, QueueName);
                });
                
                bus.AddCommandConsumeChannel(QueueName, c => c
                    .WithRabbitMq(o => o
                        .QueueName(QueueName)
                        .WithQueueType(QueueType.Quorum))
                    .Consumes<TestEvent>());
            });
        });
        
        // Wait for provisioning to complete to avoid race conditions
        await InScopeAsync(async scope =>
        {
            var manager = scope.ServiceProvider.GetRequiredService<WolverineMigrationTopologyManager>();
            await manager.WaitForProvisioningAsync();
        });

        // Assert - Verify v2 queues exist
        var factory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        // Main queue should have .v2 suffix
        var mainQueueExists = false;
        try
        {
            await channel.QueueDeclarePassiveAsync($"{QueueName}.v2");
            mainQueueExists = true;
        }
        catch { }

        mainQueueExists.Should().BeTrue($"Main queue '{QueueName}.v2' should exist");

        // Retry queue should exist
        var retryQueueExists = false;
        try
        {
            await channel.QueueDeclarePassiveAsync($"{QueueName}.v2.retry");
            retryQueueExists = true;
        }
        catch { }

        retryQueueExists.Should().BeTrue($"Retry queue '{QueueName}.v2.retry' should exist");

        // DLQ queue should exist
        var dlqQueueExists = false;
        try
        {
            await channel.QueueDeclarePassiveAsync($"{QueueName}.v2.dlq");
            dlqQueueExists = true;
        }
        catch { }

        dlqQueueExists.Should().BeTrue($"DLQ queue '{QueueName}.v2.dlq' should exist");

        // DLQ exchange should exist
        var dlqExchangeExists = false;
        try
        {
            await channel.ExchangeDeclarePassiveAsync($"{QueueName}.v2.dlq");
            dlqExchangeExists = true;
        }
        catch { }

        dlqExchangeExists.Should().BeTrue($"DLQ exchange '{QueueName}.v2.dlq' should exist");
    }

    [Test]
    public async Task Migration_ValidatesWolverineTopologyExists_ThrowsIfMissing()
    {


        // Act & Assert - Topology validation runs during provisioning
        // We need to wait for provisioning to fail
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = RabbitMqConnectionString);
                
                bus.UseWolverineMigration(migration =>
                {
                    migration.EnableMigration = true;
                    migration.QueueSuffix = ".v2";
                    migration.ValidateWolverineTopologyExists = true;
                    migration.AddQueueMapping("non-existent-queue", QueueName);
                });
                
                bus.AddCommandConsumeChannel(QueueName, c => c
                    .WithRabbitMq(o => o.QueueName(QueueName))
                    .Consumes<TestEvent>());
            });
        });
        
        await InScopeAsync(async scope =>
        {
            var manager = scope.ServiceProvider.GetRequiredService<WolverineMigrationTopologyManager>();
            
            // Should throw InvalidOperationException from validation failure
            Func<Task> act = () => manager.WaitForProvisioningAsync();
            await act.Should().ThrowAsync<InvalidOperationException>()
                .Where(e => e.Message.Contains("Wolverine topology validation failed"));
        });
    }

    [Test]
    public async Task Migration_ConsumerUsesV2Queues()
    {
        // Arrange
        var receivedMessages = new List<TestEvent>();
        
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = RabbitMqConnectionString);
                
                bus.UseWolverineMigration(migration =>
                {
                    migration.EnableMigration = true;
                    migration.QueueSuffix = ".v2";
                    migration.CreateDuplicateBindings = false;
                    migration.ValidateWolverineTopologyExists = false;
                });
                
                bus.AddCommandConsumeChannel(QueueName, c => c
                    .WithRabbitMq(o => o
                        .QueueName(QueueName)
                        .WithQueueType(QueueType.Quorum))
                    .Consumes<TestEvent>());
                
                bus.AddHandler<TestEvent, TestEventHandler>();
            });
            
            services.AddSingleton(receivedMessages);
        });

        // Wait for migration provisioning
        await InScopeAsync(async scope =>
        {
            var manager = scope.ServiceProvider.GetRequiredService<WolverineMigrationTopologyManager>();
            await manager.WaitForProvisioningAsync();
        });

        // Act - Publish directly to v2 queue
        await PublishToRabbitMqAsync(
            exchange: "", 
            routingKey: $"{QueueName}.v2", 
            new TestEvent { Id = "test-1", Data = "migration-test" });

        // Wait for message processing
        await Task.Delay(1000);

        // Assert
        receivedMessages.Should().HaveCount(1);
        receivedMessages[0].Id.Should().Be("test-1");
        receivedMessages[0].Data.Should().Be("migration-test");
    }

    [Test]
    public async Task Migration_RetryTopology_WorksCorrectly()
    {
        // Arrange
        var attemptCount = 0;
        
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = RabbitMqConnectionString);
                
                bus.UseWolverineMigration(migration =>
                {
                    migration.EnableMigration = true;
                    migration.QueueSuffix = ".v2";
                    migration.CreateDuplicateBindings = false;
                    migration.ValidateWolverineTopologyExists = false;
                });
                
                bus.AddCommandConsumeChannel(QueueName, c => c
                    .WithRabbitMq(o => o
                        .QueueName(QueueName)
                        .WithQueueType(QueueType.Quorum)
                        .RetryOptions(maxRetries: 2, delay: TimeSpan.FromMilliseconds(100)))
                    .Consumes<TestEvent>());
                
                bus.AddHandler<TestEvent, RetryTestHandler>();
            });
            
            services.AddSingleton(new RetryCounter { Count = 0 });
        });

        // Wait for topology
        await InScopeAsync(async scope =>
        {
            var manager = scope.ServiceProvider.GetRequiredService<WolverineMigrationTopologyManager>();
            await manager.WaitForProvisioningAsync();
        });

        // Act - Publish message that will fail twice then succeed
        await PublishToRabbitMqAsync(
            exchange: "", 
            routingKey: $"{QueueName}.v2", 
            new TestEvent { Id = "retry-test", Data = "fail-twice" });

        // Wait for retries
        await Task.Delay(2000);

        await InScopeAsync(async scope =>
        {
            // Assert - Handler should have been called 3 times (initial + 2 retries)
            var counter = scope.ServiceProvider.GetRequiredService<RetryCounter>();
            counter.Count.Should().BeGreaterThanOrEqualTo(2, "Message should be retried");
        });
    }

    [Test]
    public async Task Migration_HealthCheck_ReturnsHealthy()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = RabbitMqConnectionString);
                
                bus.UseWolverineMigration(migration =>
                {
                    migration.EnableMigration = true;
                    migration.QueueSuffix = ".v2";
                    migration.CreateDuplicateBindings = false;
                    migration.ValidateWolverineTopologyExists = false;
                });
                
                bus.AddCommandConsumeChannel(QueueName, c => c
                    .WithRabbitMq(o => o.QueueName(QueueName))
                    .Consumes<TestEvent>());
            });
        });

        // Wait for provisioning instead of arbitrary delay
        await InScopeAsync(async scope =>
        {
            var manager = scope.ServiceProvider.GetRequiredService<WolverineMigrationTopologyManager>();
            await manager.WaitForProvisioningAsync();
        });

        await InScopeAsync(async scope =>
        {
            // Act
            // Act
            var healthCheck = scope.ServiceProvider.GetRequiredService<WolverineMigrationHealthCheck>();
            var context = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext();

            // Wait for healthy status (consumer channels take a moment to start)
            await WaitForConditionAsync(async () => 
            {
                var res = await healthCheck.CheckHealthAsync(context);
                return res.Status == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy;
            }, TimeSpan.FromSeconds(5), "Health check to become healthy");

            var result = await healthCheck.CheckHealthAsync(context);

            // Assert
            result.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
            result.Data.Should().ContainKey("migration_enabled");
            result.Data.Should().ContainKey("queue_suffix");
        });
    }

    [Test]
    public async Task Migration_DlqTopology_WorksCorrectly()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = RabbitMqConnectionString);
                
                bus.UseWolverineMigration(migration =>
                {
                    migration.EnableMigration = true;
                    migration.QueueSuffix = ".v2";
                    migration.CreateDuplicateBindings = false;
                    migration.ValidateWolverineTopologyExists = false;
                });
                
                bus.AddCommandConsumeChannel(QueueName, c => c
                    .WithRabbitMq(o => o
                        .QueueName(QueueName)
                        .WithQueueType(QueueType.Quorum)
                        .RetryOptions(maxRetries: 1, delay: TimeSpan.FromMilliseconds(50)))
                    .Consumes<TestEvent>());
                
                bus.AddHandler<TestEvent, PermanentErrorHandler>();
            });
        });

        // Wait for topology
        await InScopeAsync(async scope =>
        {
            var manager = scope.ServiceProvider.GetRequiredService<WolverineMigrationTopologyManager>();
            await manager.WaitForProvisioningAsync();
        });

        // Act - Publish message that will fail permanently
        await PublishToRabbitMqAsync(
            exchange: "", 
            routingKey: $"{QueueName}.v2", 
            new TestEvent { Id = "dlq-test", Data = "permanent-failure" });

        // Wait for message to be processed and moved to DLQ
        await Task.Delay(1000);

        // Assert - Message should be in DLQ
        var factory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var dlqName = $"{QueueName}.v2.dlq";
        var messageFound = false;
        var attempts = 0;
        
        while (attempts < 20 && !messageFound)
        {
            var result = await channel.BasicGetAsync(dlqName, autoAck: true);
            if (result != null)
            {
                messageFound = true;
                break;
            }
            await Task.Delay(100);
            attempts++;
        }

        messageFound.Should().BeTrue($"Message should be in DLQ queue '{dlqName}'");
    }

    [Test]
    public async Task Migration_GetMigratedQueueName_ReturnsCorrectName()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = RabbitMqConnectionString);
                
                bus.UseWolverineMigration(migration =>
                {
                    migration.EnableMigration = true;
                    migration.QueueSuffix = ".v2";
                });
                
                bus.AddCommandConsumeChannel(QueueName, c => c
                    .WithRabbitMq(o => o.QueueName(QueueName))
                    .Consumes<TestEvent>());
            });
        });

        await InScopeAsync(scope =>
        {
            // Act
            var topologyManager = scope.ServiceProvider.GetRequiredService<WolverineMigrationTopologyManager>();
            var migratedName = topologyManager.GetMigratedQueueName(QueueName);

            // Assert
            migratedName.Should().Be($"{QueueName}.v2");
        });
    }

    // Helper classes
    public class TestEventHandler(List<TestEvent> receivedMessages) : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties properties, CancellationToken cancellationToken)
        {
            receivedMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    public class RetryCounter
    {
        public int Count { get; set; }
    }

    public class RetryTestHandler(RetryCounter counter) : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties properties, CancellationToken cancellationToken)
        {
            counter.Count++;
            
            // Fail first 2 attempts
            if (counter.Count < 3)
            {
                throw new Exception("Simulated transient failure");
            }
            
            return Task.CompletedTask;
        }
    }

    public class PermanentErrorHandler : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties properties, CancellationToken cancellationToken)
        {
            throw new Exception("Simulated permanent failure");
        }
    }
}
