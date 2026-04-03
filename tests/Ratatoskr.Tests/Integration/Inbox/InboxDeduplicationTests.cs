using System.Threading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Inbox;

public class InboxDeduplicationTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : InboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Inbox_Deduplication_SameMessageIdAndHandlerKey_ProcessedOnce()
    {
        // Arrange
        var callCounter = new InvocationCounter();

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<CountingHandler>("counting"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox => inbox.WithoutBackgroundProcessing()));
            });

            services.AddSingleton(callCounter);

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        const string messageId = "dedup-1";
        var sharedProps = new MessageProperties { Id = messageId };

        // Act — publish the SAME CloudEvents message ID twice (simulates duplicate delivery)
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Data = "first delivery" }, sharedProps);
        });

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Data = "duplicate delivery" }, sharedProps);
        });

        // Process inbox deterministically
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        // Assert: exactly one inbox message row and one handler status row
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            var messages = await db.Set<InboxMessageEntity>().Where(m => m.Id == messageId).ToListAsync();
            messages.Should().HaveCount(1, "duplicate delivery must not insert a second InboxMessage");

            var statuses = await db.Set<InboxHandlerStatusEntity>().Where(s => s.MessageId == messageId).ToListAsync();
            statuses.Should().HaveCount(1, "duplicate delivery must not insert a second handler status");
            statuses[0].CompletedAt.Should().NotBeNull();
        });

        callCounter.Count.Should().Be(1, "handler must execute exactly once despite duplicate delivery");
    }

    [Test]
    public async Task Inbox_ConcurrentDuplicate_ExactlyOneMessageAndStatus()
    {
        // Arrange: publish the same message ID from two concurrent tasks
        await StartTestAsync(services =>
        {
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

        const string sharedMessageId = "concurrent-dedup-1";

        // Act: rendezvous on the thread pool so Barrier.SignalAndWait does not block the async test context.
        var barrier = new Barrier(2);
        await Task.WhenAll(
            Task.Run(async () =>
            {
                await InScopeAsync(async ctx =>
                {
                    var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                    barrier.SignalAndWait();
                    await bus.PublishDirectAsync(
                        new TestEvent { Id = "business-concurrent-1" },
                        new MessageProperties { Id = sharedMessageId });
                });
            }),
            Task.Run(async () =>
            {
                await InScopeAsync(async ctx =>
                {
                    var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                    barrier.SignalAndWait();
                    await bus.PublishDirectAsync(
                        new TestEvent { Id = "business-concurrent-2" },
                        new MessageProperties { Id = sharedMessageId });
                });
            }));

        // Process inbox deterministically
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        // Assert: exactly one inbox message and one handler status
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            var messages = await db.Set<InboxMessageEntity>()
                .Where(m => m.Id == sharedMessageId).ToListAsync();
            messages.Should().HaveCount(1, "concurrent publish must not create duplicate InboxMessage rows");

            var statuses = await db.Set<InboxHandlerStatusEntity>()
                .Where(s => s.MessageId == sharedMessageId).ToListAsync();
            statuses.Should().HaveCount(1, "concurrent publish must not create duplicate handler status rows");
        });
    }

    [Test]
    public async Task Inbox_ConcurrentProcessors_EachStatusProcessedExactlyOnce()
    {
        // Arrange: publish several messages with a counting handler
        var counter = new InvocationCounter();

        await StartTestAsync(services =>
        {
            services.AddSingleton(counter);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<CountingHandler>("counting"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox => inbox.WithoutBackgroundProcessing()));
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        for (var i = 0; i < 5; i++)
        {
            var index = i;
            await InScopeAsync(async ctx =>
            {
                var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                await bus.PublishDirectAsync(
                    new TestEvent { Id = $"business-concurrent-proc-{index}" },
                    new MessageProperties { Id = $"concurrent-proc-{index}" });
            });
        }

        await WaitForInboxEntriesAsync(5);

        // Act: run two processors concurrently — they race to claim the same rows.
        // The optimistic concurrency token prevents double-processing.
        await Task.WhenAll(
            InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider)),
            InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider))
        );

        // Assert: each message was handled exactly once (no double-processing)
        counter.Count.Should().Be(5);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToArrayAsync();
            statuses.Should().HaveCount(5);
            statuses.Should().AllSatisfy(s => s.CompletedAt.Should().NotBeNull());
        });
    }
}
