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
using TUnit.Core;

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
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
        });

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);
        await InitializeDatabase();

        // Act
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            dbContext.TestEntities.Add(
                new TestEntity { Name = "Outbox Test", CreatedAt = DateTimeOffset.UtcNow }
            );

            dbContext.OutboxMessages.Add(
                new TestEvent { Id = "outbox-1", Data = "committed" },
                new MessageProperties().SetRoutingKey(DefaultRoutingKey)
            );

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
            services.AddSingleton<TestEventHandler>(handler);
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
                    options.RegisterOutbox<TestDbContext>(sp);
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
            dbContext.OutboxMessages.Add(
                new TestEvent { Id = "e2e-1", Data = "outbox->consumer" },
                props
            );

            await dbContext.SaveChangesAsync();
        });

        // Assert
        await WaitForConditionAsync(
            () =>
                handler.HandledMessages.Count > 0
                && handler.HandledMessages.Any(m => m.Id == "e2e-1"),
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
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
        });

        await InitializeDatabase();

        // Act - Save entity and outbox message in same transaction
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            var entity = new TestEntity { Name = "Test Entity", CreatedAt = DateTimeOffset.UtcNow };
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
                    options.RegisterOutbox<TestDbContext>(sp);
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
}
