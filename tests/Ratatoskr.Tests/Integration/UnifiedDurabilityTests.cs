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
using Ratatoskr.Tests.Integration.Outbox;

namespace Ratatoskr.Tests.Integration;

public class UnifiedDurabilityTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : OutboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task AddRatatoskrGeneric_PublishAsync_StagesAndDispatchesToBroker()
    {
        // Arrange: Use unified AddRatatoskr<TestDbContext> and UseRatatoskr<TestDbContext>
        await StartTestAsync(services =>
        {
            services.AddRatatoskr<TestDbContext>(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
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

        // Act: Use IRatatoskr.PublishAsync in an active transaction/scope
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var ratatoskr = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();

            await dbContext.TestEntities.AddAsync(
                new TestEntity { Name = "Unified Order", CreatedAt = DateTimeOffset.UtcNow }
            );

            // Transactional publish through IRatatoskr
            await ratatoskr.PublishAsync(
                new TestEvent { Id = "unified-1", Data = "payload-data" },
                p => p.Properties.SetRoutingKey(DefaultRoutingKey)
            );

            // Message is only staged before SaveChangesAsync
            var stagedCount = dbContext.OutboxMessages.Count;
            stagedCount.Should().Be(1);

            await dbContext.SaveChangesAsync();
        });

        // Assert: Message should be delivered to RabbitMQ
        var message = await WaitForMessageAsync(QueueName);
        message.Should().NotBeNull();
        message.RoutingKey.Should().Be(DefaultRoutingKey);
        Encoding.UTF8.GetString(message.Body.ToArray()).Should().Contain("unified-1");
    }

    [Test]
    public async Task AddRatatoskrGeneric_PublishAsync_NotSaved_NotDispatched()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr<TestDbContext>(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
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

        // Act: Publish but do NOT call SaveChangesAsync
        await InScopeAsync(async ctx =>
        {
            var ratatoskr = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await ratatoskr.PublishAsync(
                new TestEvent { Id = "discarded-1", Data = "will-not-be-saved" },
                p => p.Properties.SetRoutingKey(DefaultRoutingKey)
            );
            // dbContext.SaveChangesAsync() NOT called
        });

        // Assert: Message should NOT be delivered
        await Task.Delay(1000);
        var message = await GetMessageAsync(QueueName);
        message.Should().BeNull();
    }

    [Test]
    public async Task AutoInbox_ConsumeChannelWithKey_UsesDefaultDbContextAutomatically()
    {
        // Arrange: Consume channel registers an inbox handler with a key, but NO explicit UseInbox<T>()!
        var handler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddSingleton(handler);
            services.AddRatatoskr<TestDbContext>(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));

                bus.AddCommandPublishChannel(
                    QueueName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );

                // Auto-inbox: NO .UseInbox<TestDbContext>() call!
                bus.AddCommandConsumeChannel(
                    QueueName,
                    c =>
                        c.WithRabbitMq(o =>
                                o.WithQueueName(QueueName)
                                    .WithAutoAck(false)
                                    .WithTransientQueue()
                                    .WithQueueType(QueueType.Classic)
                            )
                            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("auto-inbox-key"))
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

        await InitializeDatabase();

        // Act: Publish via outbox
        await InScopeAsync(async ctx =>
        {
            var ratatoskr = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            await ratatoskr.PublishAsync(
                new TestEvent { Id = "auto-inbox-1", Data = "handled-by-inbox" },
                p => p.Properties.SetRoutingKey(QueueName)
            );

            await db.SaveChangesAsync();
        });

        // Assert: Handler receives the message through the inbox
        await WaitForConditionAsync(
            () => handler.HandledMessages.Exists(m => m.Id == "auto-inbox-1"),
            TimeSpan.FromSeconds(10)
        );
        handler.HandledMessages.Should().Contain(m => m.Id == "auto-inbox-1");

        // Verify inbox record was persisted in TestDbContext
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var inboxMessages = await db.Set<InboxMessageEntity>().ToListAsync();
            inboxMessages.Should().Contain(m => Encoding.UTF8.GetString(m.Content).Contains("auto-inbox-1"));

            var handlerStatuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            handlerStatuses.Should().Contain(s => s.HandlerKey == "auto-inbox-key" && s.CompletedAt != null);
        });
    }

    [Test]
    public async Task ConsumeChannel_WithoutInbox_RunsFireAndForgetEvenWithDefaultDbContext()
    {
        // Arrange: Channel explicitly opts out via WithoutInbox()
        var handler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddSingleton(handler);
            services.AddRatatoskr<TestDbContext>(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));

                bus.AddCommandPublishChannel(
                    QueueName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );

                // Explicit WithoutInbox() with parameterless WithHandler()
                bus.AddCommandConsumeChannel(
                    QueueName,
                    c =>
                        c.WithRabbitMq(o =>
                                o.WithQueueName(QueueName)
                                    .WithAutoAck(false)
                                    .WithTransientQueue()
                                    .WithQueueType(QueueType.Classic)
                            )
                            .WithoutInbox()
                            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>())
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

        await InitializeDatabase();

        // Act: Publish direct
        await InScopeAsync(async ctx =>
        {
            var ratatoskr = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await ratatoskr.PublishDirectAsync(
                new TestEvent { Id = "faf-1", Data = "fire-and-forget" },
                new MessageProperties().SetRoutingKey(QueueName)
            );
        });

        // Assert: Handler is executed directly
        await WaitForConditionAsync(
            () => handler.HandledMessages.Exists(m => m.Id == "faf-1"),
            TimeSpan.FromSeconds(10)
        );
        handler.HandledMessages.Should().Contain(m => m.Id == "faf-1");

        // Verify NO inbox records were created
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var inboxCount = await db.Set<InboxMessageEntity>().CountAsync();
            inboxCount.Should().Be(0);
        });
    }
}
