using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.RabbitMq;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Outbox;

public class OutboxProcessingTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : OutboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Outbox_UsesPerMessageSerializer_ForStagedMessage()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddSingleton<TestEventPipeMessageSerializer>();
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c =>
                        c.WithEfCore()
                            .Produces<TestEvent>(m =>
                                m.WithSerializer<TestEventPipeMessageSerializer>()
                            )
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseOutbox(outbox => outbox.WithoutBackgroundProcessing())
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
        });

        await InitializeDatabase();

        // Act
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(
                new TestEvent { Id = "outbox-pipe-1", Data = "custom-body" }
            );
            await dbContext.SaveChangesAsync();
        });

        // Assert
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().SingleAsync();
            var serializer = new TestEventPipeMessageSerializer();
            var deserialized = serializer.Deserialize<TestEvent>(entity.Content);

            deserialized.Should().NotBeNull();
            deserialized.Id.Should().Be("outbox-pipe-1");
            deserialized.Data.Should().Be("custom-body");
            entity.GetProperties().ContentType.Should().Be(serializer.ContentType);
        });
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
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c =>
                        c.WithRabbitMq(r => r.WithTopicExchange())
                            .Produces<TestEvent>(m => m.WithRoutingKey(DefaultRoutingKey))
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
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
            TimeSpan.FromSeconds(10)
        );

        // Verify all messages are marked as processed in the database
        // Note: ProcessedAt is persisted AFTER messages are sent to RabbitMQ,
        // so we need to wait for the database to be updated too.
        await WaitForConditionAsync(
            async () =>
                await InScopeAsync(async ctx =>
                {
                    var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                    var entities = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
                    return entities.Count == 3 && entities.TrueForAll(e => e.ProcessedAt != null);
                }),
            TimeSpan.FromSeconds(10)
        );
    }

    [Test]
    public async Task ProcessAllAsync_WithFailingSender_RetriesWithBackoff()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var failingSender = new FailingMessageSender(
            RabbitMqConstants.TransportName,
            failuresBeforeSuccess: 2
        );

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
            });
            // Register outbox processor without hosted service for manual control
            services.AddSingleton<OutboxTriggerInterceptor<TestDbContext>>();
            services.AddTransient<OutboxMessageProcessor<TestDbContext>>();
            services.AddSingleton<OutboxProcessor<TestDbContext>>();
            services.AddSingleton(new OutboxOptionsHolder<TestDbContext>(new OutboxOptions()));
            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
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
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
            });
            // Register outbox processor without hosted service for manual control
            services.AddSingleton<OutboxTriggerInterceptor<TestDbContext>>();
            services.AddTransient<OutboxMessageProcessor<TestDbContext>>();
            services.AddSingleton<OutboxProcessor<TestDbContext>>();
            services.AddSingleton(
                new OutboxOptionsHolder<TestDbContext>(new OutboxOptions { MaxRetries = 3 })
            );
            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
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
        for (var i = 0; i < 3; i++)
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
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c =>
                        c.WithRabbitMq(r => r.WithTopicExchange())
                            .Produces<TestEvent>(m => m.WithRoutingKey(DefaultRoutingKey))
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseOutbox(outbox => outbox.WithBatchSize(2))
                ); // Small batch
            });

            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
        });

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);
        await InitializeDatabase();

        // Stage 5 messages (more than batch size)
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            for (var i = 1; i <= 5; i++)
            {
                dbContext.OutboxMessages.Add(
                    new TestEvent
                    {
                        Data =
                            $"message {i.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    }
                );
            }
            await dbContext.SaveChangesAsync();
        });

        // Assert - All should be processed despite small batch size
        await WaitForConditionAsync(
            async () => await GetMessageCountAsync(QueueName) >= 5,
            TimeSpan.FromSeconds(10)
        );

        await WaitForConditionAsync(
            async () =>
                await InScopeAsync(async ctx =>
                {
                    var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                    var entities = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
                    return entities.TrueForAll(e => e.ProcessedAt != null);
                }),
            TimeSpan.FromSeconds(10)
        );
    }
}
