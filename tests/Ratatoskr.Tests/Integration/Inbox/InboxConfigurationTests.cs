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

public class InboxConfigurationTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : InboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Inbox_DuplicateHandlerKey_ThrowsAtStartup()
    {
        // Arrange & Act & Assert: registering two handlers with the same key should throw
        var act = async () =>
        {
            await StartTestAsync(services =>
            {
                services.AddRatatoskr(bus =>
                {
                    bus.UseLocalTransport();
                    bus.AddEventConsumeChannel("inbox-events", c => c
                        .Consumes<TestEvent>(m => m
                            .WithHandler<InboxHandlerA>("same-key")
                            .WithHandler<InboxHandlerB>("same-key"))
                        .UseInbox<TestDbContext>());
                    bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
                });

                services.AddDbContext<TestDbContext>((sp, opts) =>
                    opts.UseNpgsql(PostgresConnectionString));
            });
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Duplicate inbox handler key*same-key*");
    }

    [Test]
    public async Task Inbox_ExplicitFireAndForget_HandlerSkippedByInbox()
    {
        // Arrange: one handler with inbox, one fire-and-forget
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
                        .WithHandler<InboxHandlerA>("handler-a"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-opt-out-1" },
                new MessageProperties { Id = "opt-out-1" });
        });

        // Wait for inbox handler to complete
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var status = await db.Set<InboxHandlerStatusEntity>()
                    .SingleOrDefaultAsync(s => s.HandlerKey == "handler-a");
                return status?.CompletedAt != null;
            }),
            TimeSpan.FromSeconds(15));

        // Only one inbox handler status (InboxHandlerA) — TestEventHandler was called directly
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(1);
            statuses[0].HandlerKey.Should().Be("handler-a");
        });

        nonInboxHandler.HandledMessages.Should().ContainSingle(m => m.Id == "business-opt-out-1");
    }

    [Test]
    public async Task Inbox_HandlerKeyNoLongerRegistered_PoisonedImmediately()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("handler-a"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox =>
                    {
                        inbox.WithMaxRetries(5);
                        inbox.WithoutBackgroundProcessing();
                    }));
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-unrecoverable-1" },
                new MessageProperties { Id = "unrecoverable-1" });
        });

        await WaitForInboxEntriesAsync(1);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "InboxHandlerStatusEntity" SET "HandlerKey" = 'handler-removed-in-v2' WHERE "HandlerKey" = 'handler-a'""");
        });

        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>()
                .SingleAsync(s => s.HandlerKey == "handler-removed-in-v2");
            status.IsPoisoned.Should().BeTrue("should be poisoned immediately for unrecoverable error");
            status.ErrorCount.Should().Be(0, "ErrorCount should not be incremented for unrecoverable errors");
            status.LastError.Should().Contain("not registered");
        });
    }

    [Test]
    public async Task Inbox_MessageIdExactly200Chars_Accepted()
    {
        var longId = new string('a', 200);

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
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
                new TestEvent { Id = "business-longid-1" },
                new MessageProperties { Id = longId });
        });

        await WaitForInboxEntriesAsync(1);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var msg = await db.Set<InboxMessageEntity>().SingleAsync();
            msg.Id.Should().HaveLength(200);
        });
    }

    [Test]
    public async Task Inbox_MessageIdExceeds200Chars_Throws()
    {
        var tooLongId = new string('a', 201);

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("handler-a"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => { d.UseInbox(); d.UseOutbox(); });
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(PostgresConnectionString);
                opts.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await InitializeDatabase();

        var act = () => InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.OutboxMessages.Add(
                new TestEvent { Id = "business-toolong-1" },
                new MessageProperties { Id = tooLongId });
            await db.SaveChangesAsync();
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeds the maximum length*");
    }
}
