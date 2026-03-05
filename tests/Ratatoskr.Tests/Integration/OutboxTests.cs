using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;
using RabbitMQ.Client;
using Ratatoskr.RabbitMq;
using TUnit.Core;

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
                bus.AddEfCoreOutbox<TestDbContext>();
            });

            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);
        await InitializeDatabase();

        // Act
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            dbContext.TestEntities.Add(new TestEntity { Name = "Outbox Test", CreatedAt = DateTimeOffset.UtcNow });

            dbContext.OutboxMessages.Add(new TestEvent { Id = "outbox-1", Data = "committed" },
                new MessageProperties().SetRoutingKey(DefaultRoutingKey));

            await dbContext.SaveChangesAsync();
        });

        // Assert - Wait for the background processor to deliver the message
        var message = await WaitForMessageAsync(QueueName);
        message.Should().NotBeNull();
        message!.RoutingKey.Should().Be(DefaultRoutingKey);
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

        await InitializeDatabase();

        // Act - Stage message
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            var props = new MessageProperties().SetRoutingKey(QueueName);
            props.Transports.Add(RabbitMqConstants.TransportName);
            dbContext.OutboxMessages.Add(new TestEvent { Id = "e2e-1", Data = "outbox->consumer" },
                props);

            await dbContext.SaveChangesAsync();
        });

        // Assert
        await WaitForConditionAsync(() => handler.HandledMessages.Count > 0 && handler.HandledMessages.Any(m => m.Id == "e2e-1"), TimeSpan.FromSeconds(10));

        handler.HandledMessages.Should().Contain(m => m.Id == "e2e-1");
    }

    [Test]
    public async Task ProcessAllAsync_SendsAllPendingMessages()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>(m => m.WithRoutingKey(DefaultRoutingKey)));
                bus.AddEfCoreOutbox<TestDbContext>();
            });

            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);
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

        // Assert - Wait for all 3 messages to be delivered to the queue
        await WaitForConditionAsync(
            async () => await GetMessageCountAsync(QueueName) >= 3,
            TimeSpan.FromSeconds(10));

        // Verify all messages are marked as processed in the database
        // Note: ProcessedAt is persisted AFTER messages are sent to RabbitMQ,
        // so we need to wait for the database to be updated too.
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var entities = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
                return entities.Count == 3 && entities.All(e => e.ProcessedAt != null);
            }),
            TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task ProcessAllAsync_WithFailingSender_RetriesWithBackoff()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var failingSender = new FailingMessageSender(RabbitMqConstants.TransportName, failuresBeforeSuccess: 2);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
            });
            // Register outbox processor without hosted service for manual control
            services.AddSingleton<OutboxTelemetry>();
            services.AddSingleton<OutboxTriggerInterceptor<TestDbContext>>();
            services.AddTransient<OutboxMessageProcessor<TestDbContext>>();
            services.AddSingleton<OutboxProcessor<TestDbContext>>();
            var registry = new TypedOptionsRegistry<OutboxOptions>("outbox options");
            registry.Register(typeof(TestDbContext), new OutboxOptions());
            services.AddSingleton(registry);
            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
            // Override sender with failing sender - remove existing senders first
            services.RemoveAll<IMessageSender>();
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
            await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider);
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
            await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider);
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
            await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider);
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
        var alwaysFailingSender = new FailingMessageSender(RabbitMqConstants.TransportName); // Never succeeds

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
            });
            // Register outbox processor without hosted service for manual control
            services.AddSingleton<OutboxTelemetry>();
            services.AddSingleton<OutboxTriggerInterceptor<TestDbContext>>();
            services.AddTransient<OutboxMessageProcessor<TestDbContext>>();
            services.AddSingleton<OutboxProcessor<TestDbContext>>();
            var registry = new TypedOptionsRegistry<OutboxOptions>("outbox options");
            registry.Register(typeof(TestDbContext), new OutboxOptions { MaxRetries = 3 });
            services.AddSingleton(registry);
            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(alwaysFailingSender);
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
                await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider);
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
            var count = await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider);
            count.Should().Be(0);
        });
    }

    [Test]
    public async Task ProcessAllAsync_ProcessesInBatches()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>(m => m.WithRoutingKey(DefaultRoutingKey)));
                bus.AddEfCoreOutbox<TestDbContext>(outbox => outbox.WithBatchSize(2)); // Small batch
            });

            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);
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

        // Assert - All should be processed despite small batch size
        await WaitForConditionAsync(
            async () => await GetMessageCountAsync(QueueName) >= 5,
            TimeSpan.FromSeconds(10));

        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var entities = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
                return entities.All(e => e.ProcessedAt != null);
            }),
            TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task SaveChanges_TransactionalWithEntity_BothCommitted()
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
                bus.AddEfCoreOutbox<TestDbContext>();
            });

            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
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
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
                bus.AddEfCoreOutbox<TestDbContext>();
            });

            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await InitializeDatabase();

        // Act - Stage a message but throw before transaction commits
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
    }

    [Test]
    public async Task Outbox_MultipleDbContextsInParallel_Isolated()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>(m => m.WithRoutingKey(DefaultRoutingKey)));
                bus.AddEfCoreOutbox<TestDbContext>();
            });

            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);
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

        // Assert - Wait for both messages to be delivered
        await WaitForConditionAsync(
            async () => await GetMessageCountAsync(QueueName) >= 2,
            TimeSpan.FromSeconds(10));

        // Verify both are marked as processed
        // Note: ProcessedAt is persisted AFTER messages are sent to RabbitMQ,
        // so we need to wait for the database to be updated too.
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var entities = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
                return entities.Count == 2 && entities.All(e => e.ProcessedAt != null);
            }),
            TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Outbox_PerMessageSave_ProcessedMessageSurvivesNextFailure()
    {
        // Verifies the per-message save fix: when message 1 sends successfully and message 2 fails,
        // message 1's ProcessedAt is already persisted to the database.
        // Before the fix (batch save), both would be lost if a crash occurred after send but before SaveChanges.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sender = new SucceedThenFailSender(RabbitMqConstants.TransportName, successesBeforeFailure: 1);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
            });
            services.AddSingleton<OutboxTelemetry>();
            services.AddSingleton<OutboxTriggerInterceptor<TestDbContext>>();
            services.AddTransient<OutboxMessageProcessor<TestDbContext>>();
            services.AddSingleton<OutboxProcessor<TestDbContext>>();
            var registry = new TypedOptionsRegistry<OutboxOptions>("outbox options");
            registry.Register(typeof(TestDbContext), new OutboxOptions());
            services.AddSingleton(registry);
            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(sender);
        });

        await InitializeDatabase();

        // Stage 2 messages
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "msg-1" });
            dbContext.OutboxMessages.Add(new TestEvent { Data = "msg-2" });
            await dbContext.SaveChangesAsync();
        });

        // Act — first message succeeds, second fails
        await InScopeAsync(async ctx =>
        {
            await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider);
        });

        // Assert — first message should be processed, second should have error
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entities = await dbContext.Set<OutboxMessageEntity>()
                .OrderBy(e => e.CreatedAt).ToListAsync();

            entities.Should().HaveCount(2);
            entities[0].ProcessedAt.Should().NotBeNull("first message should be persisted as processed");
            entities[1].ProcessedAt.Should().BeNull("second message failed and should not be processed");
            entities[1].ErrorCount.Should().Be(1);
        });
    }

    [Test]
    public async Task Outbox_SendTimeout_IncreasesErrorCount()
    {
        // Verifies that a send operation exceeding the configured timeout is treated as a failure.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var slowSender = new SlowMessageSender(RabbitMqConstants.TransportName);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
            });
            services.AddSingleton<OutboxTelemetry>();
            services.AddSingleton<OutboxTriggerInterceptor<TestDbContext>>();
            services.AddTransient<OutboxMessageProcessor<TestDbContext>>();
            services.AddSingleton<OutboxProcessor<TestDbContext>>();
            var registry = new TypedOptionsRegistry<OutboxOptions>("outbox options");
            registry.Register(typeof(TestDbContext), new OutboxOptions { SendTimeout = TimeSpan.FromMilliseconds(100) });
            services.AddSingleton(registry);
            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(slowSender);
        });

        await InitializeDatabase();

        // Stage a message
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "slow-msg" });
            await dbContext.SaveChangesAsync();
        });

        // Act — process; the slow sender will be cancelled by the timeout
        await InScopeAsync(async ctx =>
        {
            await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider);
        });

        // Assert — message should have failed, not processed
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();
            entity.ProcessedAt.Should().BeNull("send timed out and should not be marked as processed");
            entity.ErrorCount.Should().Be(1);
            entity.IsPoisoned.Should().BeFalse();
        });
    }

    [Test]
    public async Task Outbox_StuckMessageDetection_ReprocessesStuckMessage()
    {
        // Verifies that a message stuck in "processing" state is recovered after the threshold.
        var startTime = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(startTime);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
            });
            services.AddSingleton<OutboxTelemetry>();
            services.AddSingleton<OutboxTriggerInterceptor<TestDbContext>>();
            services.AddTransient<OutboxMessageProcessor<TestDbContext>>();
            services.AddSingleton<OutboxProcessor<TestDbContext>>();
            var registry = new TypedOptionsRegistry<OutboxOptions>("outbox options");
            registry.Register(typeof(TestDbContext), new OutboxOptions
            {
                StuckMessageThreshold = TimeSpan.FromMinutes(5)
            });
            services.AddSingleton(registry);
            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await InitializeDatabase();

        // Stage a message and simulate it getting stuck in "processing"
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "stuck-msg" });
            await dbContext.SaveChangesAsync();
        });

        // Process normally — marks as processing and sends
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();
            entity.MarkAsProcessing(fakeTime);
            await dbContext.SaveChangesAsync();
        });

        // Advance 1 minute — below the 5-minute threshold
        fakeTime.Advance(TimeSpan.FromMinutes(1));

        // Process with stuck detection — too recent, should NOT pick up
        await InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<OutboxMessageProcessor<TestDbContext>>();

            var count = await processor.ProcessBatchAsync(includeStuckMessageDetection: true, CancellationToken.None);
            count.Should().Be(0, "message is stuck for only 1 minute — not yet considered stuck");
        });

        // Advance to 6 minutes total — past the 5-minute threshold
        fakeTime.Advance(TimeSpan.FromMinutes(5));

        // Process with stuck detection — should pick up and re-process
        await InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<OutboxMessageProcessor<TestDbContext>>();

            var count = await processor.ProcessBatchAsync(includeStuckMessageDetection: true, CancellationToken.None);
            count.Should().BeGreaterThan(0);
        });

        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();
            entity.ProcessedAt.Should().NotBeNull("stuck message should have been re-processed");
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

    private async Task<BasicGetResult?> WaitForMessageAsync(string queueName, TimeSpan? timeout = null)
    {
        BasicGetResult? result = null;
        await WaitForConditionAsync(async () =>
        {
            result = await GetMessageAsync(queueName);
            return result != null;
        }, timeout ?? TimeSpan.FromSeconds(10));
        return result;
    }

    private async Task<int> ProcessOutboxAsync<TDbContext>(IServiceProvider serviceProvider)
        where TDbContext : DbContext, IOutboxDbContext
    {
        var processor = serviceProvider.GetRequiredService<OutboxMessageProcessor<TDbContext>>();

        var totalProcessed = 0;
        while (true)
        {
            var batchProcessed = await processor.ProcessBatchAsync(includeStuckMessageDetection: false, CancellationToken.None);
            totalProcessed += batchProcessed;
            if (batchProcessed == 0) break;
        }
        return totalProcessed;
    }

    /// <summary>
    /// Sender that succeeds for the first N calls, then always fails.
    /// Used to test per-message save behavior.
    /// </summary>
    private class SucceedThenFailSender(string transportName, int successesBeforeFailure) : IMessageSender
    {
        private int _callCount;
        public string TransportName => transportName;

        public Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
        {
            _callCount++;
            if (_callCount > successesBeforeFailure)
                throw new InvalidOperationException($"Simulated failure (attempt {_callCount})");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Sender that blocks indefinitely until its cancellation token fires.
    /// Used to test send timeout behavior.
    /// </summary>
    private class SlowMessageSender(string transportName) : IMessageSender
    {
        public string TransportName => transportName;

        public async Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    #region Cleanup Tests

    [Test]
    public async Task OutboxCleanup_ProcessedMessagesOlderThanRetention_AreDeleted()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var noOpSender = new NoOpMessageSender("rabbitmq");

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
                bus.AddEfCoreOutbox<TestDbContext>(outbox =>
                {
                    outbox.WithCompletedRetention(TimeSpan.FromDays(7));
                });
            });

            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(noOpSender);

            services.AddDbContext<TestDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(PostgresConnectionString);
                opts.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await InitializeDatabase();

        // Create and process an outbox message
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.OutboxMessages.Add(
                new TestEvent { Id = "cleanup-outbox-1", Data = "test" },
                new MessageProperties { Id = "cleanup-outbox-msg" });
            await db.SaveChangesAsync();
        });

        // Wait for the outbox processor to process it
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var msg = await db.Set<OutboxMessageEntity>().SingleAsync();
                return msg.ProcessedAt != null;
            }),
            TimeSpan.FromSeconds(10));

        // Advance time past retention
        fakeTime.Advance(TimeSpan.FromDays(8));

        // Run cleanup
        await InScopeAsync(async ctx =>
        {
            var cleanup = ctx.ServiceProvider.GetRequiredService<OutboxCleanupProcessor<TestDbContext>>();
            await cleanup.RunOnceAsync(CancellationToken.None);
        });

        // Assert: processed message should be deleted
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var count = await db.Set<OutboxMessageEntity>().CountAsync();
            count.Should().Be(0);
        });
    }

    [Test]
    public async Task OutboxCleanup_ProcessedMessagesWithinRetention_AreKept()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var noOpSender = new NoOpMessageSender("rabbitmq");

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
                bus.AddEfCoreOutbox<TestDbContext>(outbox =>
                {
                    outbox.WithCompletedRetention(TimeSpan.FromDays(7));
                });
            });

            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(noOpSender);

            services.AddDbContext<TestDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(PostgresConnectionString);
                opts.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.OutboxMessages.Add(
                new TestEvent { Id = "cleanup-outbox-within-1", Data = "test" },
                new MessageProperties { Id = "cleanup-outbox-within-msg" });
            await db.SaveChangesAsync();
        });

        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var msg = await db.Set<OutboxMessageEntity>().SingleAsync();
                return msg.ProcessedAt != null;
            }),
            TimeSpan.FromSeconds(10));

        // Advance time within retention (3 days < 7 days)
        fakeTime.Advance(TimeSpan.FromDays(3));

        await InScopeAsync(async ctx =>
        {
            var cleanup = ctx.ServiceProvider.GetRequiredService<OutboxCleanupProcessor<TestDbContext>>();
            await cleanup.RunOnceAsync(CancellationToken.None);
        });

        // Assert: message should still exist
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var count = await db.Set<OutboxMessageEntity>().CountAsync();
            count.Should().Be(1);
        });
    }

    [Test]
    public async Task OutboxCleanup_WithoutCleanup_NoMessagesDeleted()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var noOpSender = new NoOpMessageSender("rabbitmq");

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
                bus.AddEfCoreOutbox<TestDbContext>(outbox =>
                {
                    outbox.WithoutCleanup();
                });
            });

            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(noOpSender);

            services.AddDbContext<TestDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(PostgresConnectionString);
                opts.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.OutboxMessages.Add(
                new TestEvent { Id = "cleanup-outbox-disabled-1", Data = "test" },
                new MessageProperties { Id = "cleanup-outbox-disabled-msg" });
            await db.SaveChangesAsync();
        });

        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var msg = await db.Set<OutboxMessageEntity>().SingleAsync();
                return msg.ProcessedAt != null;
            }),
            TimeSpan.FromSeconds(10));

        fakeTime.Advance(TimeSpan.FromDays(365));

        // WithoutCleanup means OutboxCleanupProcessor is NOT registered.
        // Verify the message still exists (cleanup service wasn't started).
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var count = await db.Set<OutboxMessageEntity>().CountAsync();
            count.Should().Be(1, "cleanup is disabled — no messages should be deleted");
        });
    }

    [Test]
    public async Task OutboxCleanup_PoisonedMessagesOlderThanRetention_AreDeleted()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var alwaysFailingSender = new FailingMessageSender(RabbitMqConstants.TransportName);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
            });

            // Manual outbox registration for control over processing
            services.AddSingleton<OutboxTelemetry>();
            services.AddSingleton<OutboxTriggerInterceptor<TestDbContext>>();
            services.AddTransient<OutboxMessageProcessor<TestDbContext>>();
            services.AddSingleton<OutboxProcessor<TestDbContext>>();
            services.AddSingleton<OutboxCleanupProcessor<TestDbContext>>();
            var registry = new TypedOptionsRegistry<OutboxOptions>("outbox options");
            registry.Register(typeof(TestDbContext), new OutboxOptions
            {
                MaxRetries = 3,
                PoisonedRetention = TimeSpan.FromDays(30)
            });
            services.AddSingleton(registry);

            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(alwaysFailingSender);

            services.AddDbContext<TestDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(PostgresConnectionString);
                opts.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await InitializeDatabase();

        // Stage message
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.OutboxMessages.Add(new TestEvent { Data = "poisoned-cleanup" });
            await db.SaveChangesAsync();
        });

        // Process until poisoned (3 retries)
        for (int i = 0; i < 3; i++)
        {
            await InScopeAsync(async ctx =>
                await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider));
            fakeTime.Advance(TimeSpan.FromSeconds(10));
        }

        // Verify message is poisoned
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await db.Set<OutboxMessageEntity>().SingleAsync();
            entity.IsPoisoned.Should().BeTrue();
        });

        // Advance past PoisonedRetention
        fakeTime.Advance(TimeSpan.FromDays(31));

        // Run cleanup
        await InScopeAsync(async ctx =>
        {
            var cleanup = ctx.ServiceProvider.GetRequiredService<OutboxCleanupProcessor<TestDbContext>>();
            await cleanup.RunOnceAsync(CancellationToken.None);
        });

        // Assert: poisoned message should be deleted
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var count = await db.Set<OutboxMessageEntity>().CountAsync();
            count.Should().Be(0);
        });
    }

    [Test]
    public async Task OutboxCleanup_SharedDatabase_DifferentRetention_OnlyDeletesOwnSourceContextMessages()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var noOpSender = new NoOpMessageSender("rabbitmq");

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
                bus.AddEfCoreOutbox<TestDbContext>(outbox =>
                {
                    outbox.WithCompletedRetention(TimeSpan.FromDays(7));
                });
                bus.AddEfCoreOutbox<SecondOutboxDbContext>(outbox =>
                {
                    outbox.WithCompletedRetention(TimeSpan.FromDays(30));
                });
            });

            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(noOpSender);

            services.AddDbContext<TestDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(PostgresConnectionString);
                opts.RegisterOutbox<TestDbContext>(sp);
            });
            services.AddDbContext<SecondOutboxDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(PostgresConnectionString);
                opts.RegisterOutbox<SecondOutboxDbContext>(sp);
            });
        });

        await InitializeDatabase();

        // Also initialize SecondOutboxDbContext tables
        await InScopeAsync(async ctx =>
        {
            var db2 = ctx.ServiceProvider.GetRequiredService<SecondOutboxDbContext>();
            await db2.Database.EnsureCreatedAsync();
        });

        // Stage messages via both DbContexts
        await InScopeAsync(async ctx =>
        {
            var db1 = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db1.OutboxMessages.Add(
                new TestEvent { Id = "outbox-ctx1-1", Data = "from TestDbContext" },
                new MessageProperties { Id = "outbox-ctx1-msg" });
            await db1.SaveChangesAsync();
        });

        await InScopeAsync(async ctx =>
        {
            var db2 = ctx.ServiceProvider.GetRequiredService<SecondOutboxDbContext>();
            db2.OutboxMessages.Add(
                new TestEvent { Id = "outbox-ctx2-1", Data = "from SecondOutboxDbContext" },
                new MessageProperties { Id = "outbox-ctx2-msg" });
            await db2.SaveChangesAsync();
        });

        // Wait for both messages to be processed
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var entities = await db.Set<OutboxMessageEntity>().ToListAsync();
                return entities.Count >= 2 && entities.All(e => e.ProcessedAt != null);
            }),
            TimeSpan.FromSeconds(10));

        // Advance 8 days — past TestDbContext retention (7d), within SecondOutboxDbContext (30d)
        fakeTime.Advance(TimeSpan.FromDays(8));

        // Run cleanup for TestDbContext only
        await InScopeAsync(async ctx =>
        {
            var cleanup = ctx.ServiceProvider.GetRequiredService<OutboxCleanupProcessor<TestDbContext>>();
            await cleanup.RunOnceAsync(CancellationToken.None);
        });

        // Assert: only TestDbContext's message deleted, SecondOutboxDbContext's message kept
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var messages = await db.Set<OutboxMessageEntity>().ToListAsync();
            messages.Should().HaveCount(1);
            messages[0].SourceContext.Should().Be(typeof(SecondOutboxDbContext).FullName);
        });

        // Advance to 31 days total — past SecondOutboxDbContext retention (30d)
        fakeTime.Advance(TimeSpan.FromDays(23));

        // Run cleanup for SecondOutboxDbContext
        await InScopeAsync(async ctx =>
        {
            var cleanup = ctx.ServiceProvider.GetRequiredService<OutboxCleanupProcessor<SecondOutboxDbContext>>();
            await cleanup.RunOnceAsync(CancellationToken.None);
        });

        // Assert: both messages now deleted
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var count = await db.Set<OutboxMessageEntity>().CountAsync();
            count.Should().Be(0);
        });
    }

    [Test]
    public async Task Outbox_DuplicateAddEfCoreOutbox_ThrowsAtStartup()
    {
        var act = () => StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
                bus.AddEfCoreOutbox<TestDbContext>();
                bus.AddEfCoreOutbox<TestDbContext>(); // Duplicate!
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(PostgresConnectionString);
                opts.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*AddEfCoreOutbox*already been called*");
    }

    private class NoOpMessageSender(string transportName) : IMessageSender
    {
        public string TransportName => transportName;
        public Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    #endregion
}
