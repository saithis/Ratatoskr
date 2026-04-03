using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Inbox;

public class InboxBasicProcessingTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : InboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Inbox_ReceivedAt_UsesInjectedTimeProvider()
    {
        var fixedInstant = new DateTimeOffset(2024, 3, 15, 14, 30, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(fixedInstant);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("handler-a"))
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
                new TestEvent { Id = "business-received-at-1" },
                new MessageProperties { Id = "received-at-1" });
        });

        await WaitForInboxEntriesAsync(1);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var message = await db.Set<InboxMessageEntity>().SingleAsync(m => m.Id == "received-at-1");
            message.ReceivedAt.Should().Be(fixedInstant);
        });
    }

    [Test]
    public async Task Inbox_AllHandlersSucceed_AllMarkedAsCompleted()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
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
    public async Task Inbox_MultipleMessageTypes_IsolatedCorrectly()
    {
        // Arrange: inbox handlers for two different message types
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel("inbox-events", c => c
                    .WithEfCore()
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
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
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
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
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
