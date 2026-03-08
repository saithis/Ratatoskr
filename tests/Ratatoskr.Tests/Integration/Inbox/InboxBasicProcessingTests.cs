using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Local;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Inbox;

public class InboxBasicProcessingTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : InboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Inbox_AllHandlersSucceed_AllMarkedAsCompleted()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m
                        .WithHandler<InboxHandlerA>("handler-a")
                        .WithHandler<InboxHandlerB>("handler-b"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-all-succeed-1", Data = "test" },
                new MessageProperties { Id = "all-succeed-1" });
        });

        // Assert — wait for both handler statuses to have CompletedAt set
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
                return statuses.Count == 2 && statuses.All(s => s.CompletedAt != null);
            }),
            TimeSpan.FromSeconds(15));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var message = await db.Set<InboxMessageEntity>().SingleAsync();
            message.Id.Should().Be("all-succeed-1");

            var statuses = await db.Set<InboxHandlerStatusEntity>()
                .OrderBy(s => s.HandlerKey).ToListAsync();
            statuses.Should().HaveCount(2);
            statuses[0].HandlerKey.Should().Be("handler-a");
            statuses[1].HandlerKey.Should().Be("handler-b");
            statuses.Should().AllSatisfy(s =>
            {
                s.CompletedAt.Should().NotBeNull();
                s.IsPoisoned.Should().BeFalse();
                s.ErrorCount.Should().Be(0);
            });
        });
    }

    [Test]
    public async Task Inbox_MixedHandlers_InboxAndNonInboxHandlersBothRun()
    {
        // Arrange
        var nonInboxHandler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddSingleton<TestEventHandler>(nonInboxHandler);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m
                        .WithHandler<TestEventHandler>("faf-handler", h => h.WithoutInbox())
                        .WithHandler<InboxHandlerA>("inbox-handler"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-mixed-1", Data = "mixed handlers" },
                new MessageProperties { Id = "mixed-1" });
        });

        // Wait for the inbox handler status to be completed
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var status = await db.Set<InboxHandlerStatusEntity>()
                    .SingleOrDefaultAsync(s => s.HandlerKey == "inbox-handler");
                return status?.CompletedAt != null;
            }),
            TimeSpan.FromSeconds(15));

        // Non-inbox handler should have been called synchronously via LocalTransportConsumer
        nonInboxHandler.HandledMessages.Should().ContainSingle(m => m.Id == "business-mixed-1");

        // Inbox handler should have been completed via InboxProcessor
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.HandlerKey.Should().Be("inbox-handler");
            status.CompletedAt.Should().NotBeNull();
            status.ErrorCount.Should().Be(0);
        });
    }

    [Test]
    public async Task Inbox_ZeroInboxHandlers_MessagesDispatchedNormally()
    {
        // Arrange: channel with UseInbox but no handlers registered with an inbox key
        var nonInboxHandler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddSingleton<TestEventHandler>(nonInboxHandler);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("faf-handler", h => h.WithoutInbox()))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-zero-inbox-1", Data = "no inbox handlers" },
                new MessageProperties { Id = "zero-inbox-1" });
        });

        // Assert: non-inbox handler was called synchronously
        await WaitForConditionAsync(
            () => Task.FromResult(nonInboxHandler.HandledMessages.Any()),
            TimeSpan.FromSeconds(5));

        nonInboxHandler.HandledMessages.Should().ContainSingle(m => m.Id == "business-zero-inbox-1");

        // No inbox rows should have been created
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var inboxMessages = await db.Set<InboxMessageEntity>().ToListAsync();
            inboxMessages.Should().BeEmpty("no inbox handlers means no inbox rows");

            var handlerStatuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            handlerStatuses.Should().BeEmpty();
        });
    }

    [Test]
    public async Task Inbox_MultipleMessageTypes_IsolatedCorrectly()
    {
        // Arrange: inbox handlers for two different message types
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c
                    .WithLocal()
                    .Produces<TestEvent>()
                    .Produces<OrderCreatedEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("test-handler"))
                    .Consumes<OrderCreatedEvent>(m => m.WithHandler<OrderCreatedInboxHandler>("order-handler"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Act: publish both message types
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-multi-test-1" },
                new MessageProperties { Id = "multi-test-1" });
            await bus.PublishDirectAsync(
                new OrderCreatedEvent { OrderId = Guid.NewGuid(), Amount = 42.00m },
                new MessageProperties { Id = "multi-order-1" });
        });

        // Assert: both handler types have completed statuses
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
                return statuses.Count == 2 && statuses.All(s => s.CompletedAt != null);
            }),
            TimeSpan.FromSeconds(15));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>()
                .OrderBy(s => s.HandlerKey).ToListAsync();
            statuses.Should().HaveCount(2);
            statuses.Should().Contain(s => s.HandlerKey == "test-handler" && s.MessageId == "multi-test-1");
            statuses.Should().Contain(s => s.HandlerKey == "order-handler" && s.MessageId == "multi-order-1");

            var messages = await db.Set<InboxMessageEntity>().OrderBy(m => m.Id).ToListAsync();
            messages.Should().HaveCount(2, "two different messages for two different types");
            messages.Should().Contain(m => m.Id == "multi-test-1");
            messages.Should().Contain(m => m.Id == "multi-order-1");
        });
    }

    [Test]
    public async Task Inbox_ContentRoundTrip_DeserializesCorrectly()
    {
        // Arrange: verify message body and properties survive serialization/deserialization
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("roundtrip"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox => inbox.WithoutBackgroundProcessing()));
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        const string cloudEventsId = "ce-roundtrip-1";
        const string businessId = "business-roundtrip-1";
        const string data = "special chars: <>&\"' éèê";

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = businessId, Data = data },
                new MessageProperties { Id = cloudEventsId });
        });

        await WaitForInboxEntriesAsync(1);

        // Verify the stored message can be deserialized back
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var inboxMsg = await db.Set<InboxMessageEntity>().SingleAsync(m => m.Id == cloudEventsId);
            inboxMsg.Id.Should().Be(cloudEventsId, "entity ID is the CloudEvents ID");

            var props = inboxMsg.GetProperties();
            props.Id.Should().Be(cloudEventsId, "properties ID should match entity ID");
            props.Type.Should().Be("test.event");

            var serializer = ctx.ServiceProvider.GetRequiredService<IMessageSerializer>();
            var deserialized = serializer.Deserialize(inboxMsg.Content, typeof(TestEvent)) as TestEvent;
            deserialized.Should().NotBeNull();
            deserialized!.Id.Should().Be(businessId, "business ID preserved in serialized content");
            deserialized.Data.Should().Be(data);
        });
    }

    [Test]
    public async Task Inbox_HandlerStatusEntity_HasCreatedAtTimestamp()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero));

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("with-created"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox => inbox.WithoutBackgroundProcessing()));
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-created-1" },
                new MessageProperties { Id = "created-1" });
        });

        await WaitForInboxEntriesAsync(1);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.CreatedAt.Should().Be(new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero));
        });
    }

    private class OrderCreatedInboxHandler : IMessageHandler<OrderCreatedEvent>
    {
        public Task HandleAsync(OrderCreatedEvent message, MessageProperties props, CancellationToken ct)
            => Task.CompletedTask;
    }
}
