using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.EfCore.Testing;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Testing;

namespace Ratatoskr.Tests.Integration;

public class OutboxTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string ExchangeName => $"outbox-test-{TestId}";
    private string QueueName => $"outbox-queue-{TestId}";
    private string DefaultRoutingKey => "test.event";

    [Test]
    public async Task Outbox_TransactionCommitted_MessagePublished()
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

            services.AddTestDbContext(PostgresConnectionString);
            services.AddTestOutbox<TestDbContext>();
        });

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);

        // Ensure DB Created
        await InitializeDatabase();

        // Act
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            // Add entity and event
            dbContext.TestEntities.Add(new TestEntity { Name = "Outbox Test", CreatedAt = DateTimeOffset.UtcNow });

            dbContext.OutboxMessages.Add(new TestEvent { Id = "outbox-1", Data = "committed" },
                new MessageProperties().SetRoutingKey(DefaultRoutingKey));

            await dbContext.SaveChangesAsync();
        });

        // Process Outbox
        await InScopeAsync(async ctx =>
        {
            var processedCount = await OutboxTestHelper.ProcessAllAsync<TestDbContext>(ctx.ServiceProvider);
            processedCount.Should().Be(1);
        });

        // Assert
        var message = await GetMessageAsync(QueueName);
        message.Should().NotBeNull();
        message.RoutingKey.Should().Be(DefaultRoutingKey);
        message.Body.Should().NotBeNull();
        Encoding.UTF8.GetString(message.Body.ToArray()).Should().Contain("outbox-1");
    }

    [Test]
    public async Task Outbox_ToConsumer_EndToEnd()
    {
        // Arrange
        var handler = new TestEventHandler();
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddCommandConsumeChannel(QueueName, c => c
                    .WithRabbitMq(o => o.WithQueueName(QueueName).WithAutoAck(false).WithTransientQueue()
                        .WithQueueType(QueueType.Classic))
                    .Consumes<TestEvent>());
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
                bus.AddEfCoreOutbox<TestDbContext>();
            });

            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
        });

        // Ensure DB Created
        await InitializeDatabase();

        // Act - Stage message
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            // We are sending Command style to the queue name
            dbContext.OutboxMessages.Add(new TestEvent { Id = "e2e-1", Data = "outbox->consumer" },
                new MessageProperties().SetExchange(QueueName));

            await dbContext.SaveChangesAsync();
        });

        // Assert
        await WaitForConditionAsync(() => handler.HandledMessages.Count > 0 && handler.HandledMessages.Any(m => m.Id == "e2e-1"), TimeSpan.FromSeconds(5));

        handler.HandledMessages.Should().Contain(m => m.Id == "e2e-1");
    }

    [Test]
    public async Task ProcessAllAsync_SendsAllPendingMessages()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddTestRatatoskr(bus => bus.AddEventPublishChannel("test", c => c.Produces<TestEvent>()));
            services.AddTestDbContext(PostgresConnectionString);
            services.AddTestOutbox<TestDbContext>();
        });

        await InitializeDatabase();

        // Stage messages
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "message 1" });
            dbContext.OutboxMessages.Add(new TestEvent { Data = "message 2" });
            dbContext.OutboxMessages.Add(new TestEvent { Data = "message 3" });
            await dbContext.SaveChangesAsync();
        });

        // Act
        int processedCount = 0;
        await InScopeAsync(async ctx =>
        {
            processedCount = await OutboxTestHelper.ProcessAllAsync<TestDbContext>(ctx.ServiceProvider);
        });

        // Assert
        await InScopeAsync(ctx =>
        {
            var sink = ctx.ServiceProvider.GetRequiredService<MessageSink>();
            processedCount.Should().Be(3);
            sink.Messages.Should().HaveCount(3);
        });

        // Verify all messages are marked as processed
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entities = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
            entities.All(e => e.ProcessedAt != null).Should().BeTrue();
        });
    }

    [Test]
    public async Task ProcessAllAsync_WithFailingSender_RetriesWithBackoff()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var failingSender = new FailingMessageSender(failuresBeforeSuccess: 2);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddTestRatatoskr(bus => bus.AddEventPublishChannel("test", c => c.Produces<TestEvent>()));
            services.AddTestDbContext(PostgresConnectionString);
            services.AddTestOutbox<TestDbContext>();
            // Replace MessageSink with FailingMessageSender
            services.AddSingleton<IMessageSender>(failingSender);
        });

        await InitializeDatabase();

        // Stage message
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "test" });
            await dbContext.SaveChangesAsync();
        });

        // Act - First attempt (will fail)
        await InScopeAsync(async ctx =>
        {
            await OutboxTestHelper.ProcessAllAsync<TestDbContext>(ctx.ServiceProvider);
        });

        // Assert - Message should not be processed, should have retry scheduled
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();

            entity.ProcessedAt.Should().BeNull();
            entity.ErrorCount.Should().Be(1);
            entity.NextAttemptAt.Should().NotBeNull();
            entity.IsPoisoned.Should().BeFalse();
        });

        // Advance time past retry delay
        fakeTime.Advance(TimeSpan.FromSeconds(3));

        // Act - Second attempt (will also fail)
        await InScopeAsync(async ctx =>
        {
            await OutboxTestHelper.ProcessAllAsync<TestDbContext>(ctx.ServiceProvider);
        });

        // Assert
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();

            entity.ErrorCount.Should().Be(2);
        });

        // Advance time again
        fakeTime.Advance(TimeSpan.FromSeconds(5));

        // Act - Third attempt (will succeed)
        await InScopeAsync(async ctx =>
        {
            await OutboxTestHelper.ProcessAllAsync<TestDbContext>(ctx.ServiceProvider);
        });

        // Assert - Should now be processed
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();

            entity.ProcessedAt.Should().NotBeNull();
            entity.ErrorCount.Should().Be(2); // Still 2, last attempt succeeded
            failingSender.CallCount.Should().Be(3);
        });
    }

    [Test]
    public async Task ProcessAllAsync_AfterMaxRetries_MarksPoisoned()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var alwaysFailingSender = new FailingMessageSender(); // Never succeeds

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddTestRatatoskr(bus => bus.AddEventPublishChannel("test", c => c.Produces<TestEvent>()));
            services.AddTestDbContext(PostgresConnectionString);
            services.AddTestOutbox<TestDbContext>(outbox => outbox.WithMaxRetries(3));
            services.AddSingleton<global::Ratatoskr.Core.IMessageSender>(alwaysFailingSender);
        });

        await InitializeDatabase();

        // Stage message
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "test" });
            await dbContext.SaveChangesAsync();
        });

        // Act - Try processing 3 times (max retries)
        for (int i = 0; i < 3; i++)
        {
            await InScopeAsync(async ctx =>
            {
                await OutboxTestHelper.ProcessAllAsync<TestDbContext>(ctx.ServiceProvider);
            });

            // Advance time for next retry
            fakeTime.Advance(TimeSpan.FromSeconds(10));
        }

        // Assert - Should be marked as poisoned
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();

            entity.IsPoisoned.Should().BeTrue();
            entity.ProcessedAt.Should().BeNull();
            entity.ErrorCount.Should().Be(3);
            entity.NextAttemptAt.Should().BeNull(); // No more retries
        });

        // Try processing again - should not attempt poisoned message
        await InScopeAsync(async ctx =>
        {
            var count = await OutboxTestHelper.ProcessAllAsync<TestDbContext>(ctx.ServiceProvider);
            count.Should().Be(0);
        });
    }

    [Test]
    public async Task ProcessAllAsync_ProcessesInBatches()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddTestRatatoskr(bus => bus.AddEventPublishChannel("test", c => c.Produces<TestEvent>()));
            services.AddTestDbContext(PostgresConnectionString);
            services.AddTestOutbox<TestDbContext>(outbox => outbox.WithBatchSize(2)); // Small batch
        });

        await InitializeDatabase();

        // Stage 5 messages (more than batch size)
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            for (int i = 1; i <= 5; i++)
            {
                dbContext.OutboxMessages.Add(new TestEvent { Data = $"message {i}" });
            }
            await dbContext.SaveChangesAsync();
        });

        // Act
        await InScopeAsync(async ctx =>
        {
            await OutboxTestHelper.ProcessAllAsync<TestDbContext>(ctx.ServiceProvider);
        });

        // Assert - All should be processed despite small batch size
        await InScopeAsync(ctx =>
        {
            var sink = ctx.ServiceProvider.GetRequiredService<MessageSink>();
            sink.Messages.Should().HaveCount(5);
        });

        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entities = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
            entities.All(e => e.ProcessedAt != null).Should().BeTrue();
        });
    }

    [Test]
    public async Task SaveChanges_TransactionalWithEntity_BothCommitted()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddTestRatatoskr(bus => bus.AddEventPublishChannel("test", c => c.Produces<TestEvent>()));
            services.AddTestDbContext(PostgresConnectionString);
            services.AddTestOutbox<TestDbContext>();
        });

        await InitializeDatabase();

        // Act - Save entity and outbox message in same transaction
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            var entity = new TestEntity
            {
                Name = "Test Entity",
                CreatedAt = DateTimeOffset.UtcNow
            };
            dbContext.TestEntities.Add(entity);

            dbContext.OutboxMessages.Add(new TestEvent { Data = "event for entity" });

            await dbContext.SaveChangesAsync();
        });

        // Assert - Both should be saved
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            var entities = await dbContext.TestEntities.ToListAsync();
            entities.Should().HaveCount(1);
            entities[0].Name.Should().Be("Test Entity");

            var outboxMessages = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
            outboxMessages.Should().HaveCount(1);
        });
    }


    [Test]
    public async Task Outbox_RollbackTransaction_MessageNotPublished()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddTestRatatoskr(bus => bus.AddEventPublishChannel("test", c => c.Produces<TestEvent>()));
            services.AddTestDbContext(PostgresConnectionString);
            services.AddTestOutbox<TestDbContext>();
        });

        await InitializeDatabase();

        // Act - Stage a message but throw before SaveChanges completes
        try
        {
            await InScopeAsync(async ctx =>
            {
                var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                await using var transaction = await dbContext.Database.BeginTransactionAsync();

                dbContext.OutboxMessages.Add(new TestEvent { Data = "should not be saved" });

                // The interceptor will run here and add the OutboxMessageEntity to the DbContext.
                await dbContext.SaveChangesAsync();

                // Simulate a subsequent failure that prevents the transaction from being committed.
                throw new InvalidOperationException("Simulated failure before commit");
            });
        }
        catch (InvalidOperationException)
        {
            // Expected exception
        }

        // Assert - No outbox entities should exist
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entities = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
            entities.Should().BeEmpty();
        });

        // Verify processing finds nothing
        await InScopeAsync(async ctx =>
        {
            var count = await OutboxTestHelper.ProcessAllAsync<TestDbContext>(ctx.ServiceProvider);
            count.Should().Be(0);
        });
    }

    [Test]
    public async Task Outbox_MultipleDbContextsInParallel_Isolated()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddTestRatatoskr(bus => bus.AddEventPublishChannel("test", c => c.Produces<TestEvent>()));
            services.AddTestDbContext(PostgresConnectionString);
            services.AddTestOutbox<TestDbContext>();
        });

        await InitializeDatabase();

        // Act - Two parallel scopes stage different messages
        var task1 = InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "scope-1-msg" });
            await dbContext.SaveChangesAsync();
        });

        var task2 = InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "scope-2-msg" });
            await dbContext.SaveChangesAsync();
        });

        await Task.WhenAll(task1, task2);

        // Assert - Both messages should exist independently
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entities = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
            entities.Should().HaveCount(2);
        });

        // Process all and verify both are sent
        await InScopeAsync(async ctx =>
        {
            var processedCount = await OutboxTestHelper.ProcessAllAsync<TestDbContext>(ctx.ServiceProvider);
            processedCount.Should().Be(2);
        });

        await InScopeAsync(ctx =>
        {
            var sink = ctx.ServiceProvider.GetRequiredService<MessageSink>();
            sink.Messages.Should().HaveCount(2);
        });
    }

    private async Task InitializeDatabase()
    {
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            await dbContext.Database.EnsureCreatedAsync();
        });
    }
}
