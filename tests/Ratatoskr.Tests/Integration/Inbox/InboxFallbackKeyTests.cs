using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Inbox;

public class InboxFallbackKeyTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : InboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Inbox_FallbackKey_ProcessesExistingEntriesWithOldKey()
    {
        // Arrange: register handler with new key + fallback to old key
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m
                        .WithHandler<InboxHandlerA>("handler-a-v2", "handler-a-v1"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox => inbox.WithoutBackgroundProcessing()));
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Simulate a message that was written by the old deployment with "handler-a-v1"
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var serializer = ctx.ServiceProvider.GetRequiredService<IMessageSerializer>();
            var body = serializer.Serialize(new TestEvent { Id = "old-msg-1", Data = "old data" });
            var message = InboxMessageEntity.Create(
                "old-msg-1", "efcore", body,
                new MessageProperties { Id = "old-msg-1", Type = "test.event" },
                fakeTime);
            db.Set<InboxMessageEntity>().Add(message);
            db.Set<InboxHandlerStatusEntity>().Add(
                InboxHandlerStatusEntity.Create("old-msg-1", "handler-a-v1", fakeTime));
            await db.SaveChangesAsync();
        });

        // Act — process the inbox
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        // Assert — the old entry should be completed, NOT poisoned
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.HandlerKey.Should().Be("handler-a-v1");
            status.CompletedAt.Should().NotBeNull("fallback key should allow the handler to process");
            status.IsPoisoned.Should().BeFalse("should NOT be poisoned when fallback key matches");
        });
    }

    [Test]
    public async Task Inbox_FallbackKey_NewEntriesUseCurrentKey()
    {
        // Arrange: register handler with new key + fallback
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m
                        .WithHandler<InboxHandlerA>("handler-a-v2", "handler-a-v1"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox => inbox.WithoutBackgroundProcessing()));
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Act — publish a new message (simulates the new deployment creating entries)
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-new-1" },
                new MessageProperties { Id = "new-msg-1" });
        });

        await WaitForInboxEntriesAsync(1);

        // Assert — the new entry should use the CURRENT key, not fallback
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.HandlerKey.Should().Be("handler-a-v2", "new entries must use the current key, not the fallback");
        });
    }

    [Test]
    public async Task Inbox_FallbackKeyConflictsWithPrimaryKey_ThrowsAtStartup()
    {
        // Arrange & Act & Assert: a fallback key that matches another handler's primary key should throw
        var act = async () =>
        {
            await StartTestAsync(services =>
            {
                services.AddRatatoskr(bus =>
                {
                    bus.AddEventConsumeChannel("inbox-events", c => c
                        .Consumes<TestEvent>(m => m
                            .WithHandler<InboxHandlerA>("handler-a")
                            .WithHandler<InboxHandlerB>("handler-b", "handler-a"))
                        .UseInbox<TestDbContext>());
                    bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
                });

                services.AddDbContext<TestDbContext>((sp, opts) =>
                    opts.UseNpgsql(PostgresConnectionString));
            });
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Duplicate inbox handler key*handler-a*");
    }

    [Test]
    public async Task Inbox_MultipleFallbackKeys_AllProcessed()
    {
        // Arrange: handler renamed twice: v1 → v2 → v3
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m
                        .WithHandler<InboxHandlerA>("handler-a-v3", "handler-a-v1", "handler-a-v2"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox => inbox.WithoutBackgroundProcessing()));
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Insert entries with both old keys
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var serializer = ctx.ServiceProvider.GetRequiredService<IMessageSerializer>();

            var body1 = serializer.Serialize(new TestEvent { Id = "v1-msg", Data = "v1 data" });
            var msg1 = InboxMessageEntity.Create(
                "v1-msg", "efcore", body1,
                new MessageProperties { Id = "v1-msg", Type = "test.event" }, fakeTime);
            db.Set<InboxMessageEntity>().Add(msg1);
            db.Set<InboxHandlerStatusEntity>().Add(
                InboxHandlerStatusEntity.Create("v1-msg", "handler-a-v1", fakeTime));

            var body2 = serializer.Serialize(new TestEvent { Id = "v2-msg", Data = "v2 data" });
            var msg2 = InboxMessageEntity.Create(
                "v2-msg", "efcore", body2,
                new MessageProperties { Id = "v2-msg", Type = "test.event" }, fakeTime);
            db.Set<InboxMessageEntity>().Add(msg2);
            db.Set<InboxHandlerStatusEntity>().Add(
                InboxHandlerStatusEntity.Create("v2-msg", "handler-a-v2", fakeTime));

            await db.SaveChangesAsync();
        });

        // Act
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        // Assert — both entries should be completed
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>()
                .OrderBy(s => s.HandlerKey).ToListAsync();
            statuses.Should().HaveCount(2);
            statuses.Should().AllSatisfy(s =>
            {
                s.CompletedAt.Should().NotBeNull();
                s.IsPoisoned.Should().BeFalse();
            });
        });
    }
}
