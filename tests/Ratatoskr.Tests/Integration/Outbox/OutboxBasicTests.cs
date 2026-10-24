using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.RabbitMq;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Outbox;

public class OutboxBasicTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : OutboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Outbox_TransactionCommitted_MessagePublished()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.UseRatatoskr<TestDbContext>(sp);
                }
            );
        });

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);
        await InitializeDatabase();

        // Act
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            await dbContext.TestEntities.AddAsync(
                new TestEntity { Name = "Outbox Test", CreatedAt = DateTimeOffset.UtcNow }
            );

            dbContext.Outbox.Add(
                new TestEvent { Id = "outbox-1", Data = "committed" },
                new MessageProperties().SetRoutingKey(DefaultRoutingKey)
            );

            await dbContext.SaveChangesAsync();
        });

        // Assert - Wait for the background processor to deliver the message
        var message = await WaitForMessageAsync(QueueName);
        message.Should().NotBeNull();
        message.RoutingKey.Should().Be(DefaultRoutingKey);
        Encoding.UTF8.GetString(message.Body.ToArray()).Should().Contain("outbox-1");
    }

    [Test]
    public async Task Outbox_BatchWithOnlyPoisonedMessages_ProcessorContinuesWithNextBatch()
    {
        // Arrange: one message per batch and a polling interval far beyond the test timeout,
        // so the second message is only sent if the processor keeps going after the first batch.
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseOutbox(o => o.WithBatchSize(1).WithPollingInterval(TimeSpan.FromHours(1)))
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.UseRatatoskr<TestDbContext>(sp);
                }
            );
        });

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);
        await InitializeDatabase();

        // Act: insert both rows in one transaction (bypassing outbox staging, so no processing
        // run can see only one of them). The older row targets a transport without a sender and
        // is poisoned in the first batch; the newer one is only sent if a second batch runs.
        Guid poisonedId = default;
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var preparer = ctx.ServiceProvider.GetRequiredService<OutgoingMessagePreparer>();

            var unroutable = OutboxMessageEntity.Create(
                Encoding.UTF8.GetBytes("{}"),
                new MessageProperties
                {
                    Id = "unroutable",
                    Type = "test.event",
                    Source = "/test",
                    Time = time.GetUtcNow(),
                },
                time,
                "missing-transport"
            );
            poisonedId = unroutable.Id;
            await dbContext.Set<OutboxMessageEntity>().AddAsync(unroutable);

            var (props, body) = preparer.Prepare(
                new TestEvent { Id = "after-poison", Data = "second batch" },
                typeof(TestEvent),
                new MessageProperties().SetRoutingKey(DefaultRoutingKey)
            );
            await dbContext
                .Set<OutboxMessageEntity>()
                .AddAsync(
                    OutboxMessageEntity.Create(body, props, time, RabbitMqConstants.TransportName)
                );

            await dbContext.SaveChangesAsync();
        });
        await Services.GetRequiredService<OutboxProcessor<TestDbContext>>().TriggerAsync();

        // Assert
        var message = await WaitForMessageAsync(QueueName);
        message.Should().NotBeNull("the processor must not stop after a batch that only poisoned");
        Encoding.UTF8.GetString(message.Body.ToArray()).Should().Contain("after-poison");

        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var poisoned = await dbContext.Set<OutboxMessageEntity>().SingleAsync(x => x.Id == poisonedId);
            poisoned.IsPoisoned.Should().BeTrue();
        });
    }

    [Test]
    public async Task Outbox_ToConsumer_EndToEnd()
    {
        // Arrange
        var handler = new TestEventHandler();
        await StartTestAsync(services =>
        {
            services.AddSingleton(handler);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddCommandConsumeChannel(
                    QueueName,
                    c =>
                        c.WithRabbitMq(o =>
                                o.WithQueueName(QueueName)
                                    .WithAutoAck(false)
                                    .WithTransientQueue()
                                    .WithQueueType(QueueType.Classic)
                            )
                            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>())
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.UseRatatoskr<TestDbContext>(sp);
                }
            );
        });

        await InitializeDatabase();

        // Act - Stage message
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            var props = new MessageProperties().SetRoutingKey(QueueName);
            props.Transports.Add(RabbitMqConstants.TransportName);
            dbContext.Outbox.Add(
                new TestEvent { Id = "e2e-1", Data = "outbox->consumer" },
                props
            );

            await dbContext.SaveChangesAsync();
        });

        // Assert
        await WaitForConditionAsync(
            () =>
                handler.HandledMessages.Count > 0
                && handler.HandledMessages.Exists(m => m.Id == "e2e-1"),
            TimeSpan.FromSeconds(10)
        );

        handler.HandledMessages.Should().Contain(m => m.Id == "e2e-1");
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
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.UseRatatoskr<TestDbContext>(sp);
                }
            );
        });

        await InitializeDatabase();

        // Act - Save entity and outbox message in same transaction
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            var entity = new TestEntity { Name = "Test Entity", CreatedAt = DateTimeOffset.UtcNow };
            await dbContext.TestEntities.AddAsync(entity);

            dbContext.Outbox.Add(new TestEvent { Data = "event for entity" });

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
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.UseRatatoskr<TestDbContext>(sp);
                }
            );
        });

        await InitializeDatabase();

        // Act - Stage a message but throw before transaction commits
        try
        {
            await InScopeAsync(async ctx =>
            {
                var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                await using var transaction = await dbContext.Database.BeginTransactionAsync();

                dbContext.Outbox.Add(new TestEvent { Data = "should not be saved" });

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
}
