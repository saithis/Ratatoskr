using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Inbox;

public class InboxBatchAndEndToEndTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : InboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Inbox_AccumulatedMessages_ProcessedAfterManualTrigger()
    {
        // Arrange: messages accumulate while background processing is disabled
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "inbox-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "inbox-events",
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("handler-a"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(inbox => inbox.WithoutBackgroundProcessing())
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        // Publish 5 messages while processing is disabled
        for (var i = 0; i < 5; i++)
        {
            await InScopeAsync(async ctx =>
            {
                var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                await bus.PublishDirectAsync(
                    new TestEvent { Id = $"business-accum-{i}" },
                    new MessageProperties { Id = $"accum-{i}" }
                );
            });
        }

        await WaitForInboxEntriesAsync(5);

        // Verify nothing is completed yet
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(5);
            statuses.Should().AllSatisfy(s => s.CompletedAt.Should().BeNull());
        });

        // Act: process all accumulated messages manually
        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(ctx.ServiceProvider);
            processed.Should().Be(5);
        });

        // Assert: all are completed
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(5);
            statuses.Should().AllSatisfy(s => s.CompletedAt.Should().NotBeNull());
        });
    }

    [Test]
    public async Task Inbox_BatchBoundary_ProcessesAcrossMultipleBatches()
    {
        // Arrange: BatchSize = 3, publish 5 messages
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "inbox-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "inbox-events",
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("handler-a"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(inbox =>
                    {
                        inbox.WithBatchSize(3);
                        inbox.WithoutBackgroundProcessing();
                    })
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        // Publish 5 messages
        for (var i = 0; i < 5; i++)
        {
            await InScopeAsync(async ctx =>
            {
                var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                await bus.PublishDirectAsync(
                    new TestEvent { Id = $"business-batch-{i}" },
                    new MessageProperties { Id = $"batch-{i}" }
                );
            });
        }

        await WaitForInboxEntriesAsync(5);

        // Act: process all batches (should take at least 2 batch iterations: 3 + 2)
        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(ctx.ServiceProvider);
            processed.Should().Be(5, "all 5 messages should be processed across multiple batches");
        });

        // Assert: all 5 are completed
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(5);
            statuses.Should().AllSatisfy(s => s.CompletedAt.Should().NotBeNull());
        });
    }

    [Test]
    public async Task Inbox_StuckDetection_ReprocessesStuckHandlerStatus()
    {
        // Arrange
        var startTime = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(startTime);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "inbox-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "inbox-events",
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("handler-a"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(inbox =>
                    {
                        inbox.WithStuckMessageThreshold(TimeSpan.FromMinutes(5));
                        inbox.WithoutBackgroundProcessing();
                    })
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        // Insert an inbox message and a handler status that simulates a mid-processing crash:
        // ProcessingStartedAt is set to NOW, simulating a handler that started but never completed.
        const string messageId = "stuck-1";

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var timeProvider = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var serializer = ctx.ServiceProvider.GetRequiredService<IMessageSerializer>();

            var testEvent = new TestEvent { Id = messageId, Data = "stuck message" };
            var body = serializer.Serialize(testEvent);
            var props = new MessageProperties { Id = messageId, Type = "test.event" };

            db.Set<InboxMessageEntity>()
                .Add(
                    InboxMessageEntity.Create(
                        messageId,
                        EfCoreTransportConstants.TransportName,
                        body,
                        props,
                        timeProvider
                    )
                );

            var status = InboxHandlerStatusEntity.Create(messageId, "handler-a", timeProvider);
            status.MarkAsProcessing(timeProvider); // Simulate in-progress at startTime
            db.Set<InboxHandlerStatusEntity>().Add(status);

            await db.SaveChangesAsync();
        });

        // Advance 1 minute: status is "stuck" for 1 minute — below the 5-minute threshold
        fakeTime.Advance(TimeSpan.FromMinutes(1));

        // Processing with stuck detection: too recent, should NOT be picked up
        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(
                ctx.ServiceProvider,
                includeStuckDetection: true
            );
            processed
                .Should()
                .Be(0, "handler started only 1 minute ago — not yet considered stuck");
        });

        // Advance to 6 minutes since ProcessingStartedAt — past the 5-minute threshold
        fakeTime.Advance(TimeSpan.FromMinutes(5));

        // Processing with stuck detection: old enough, should be picked up and completed
        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(
                ctx.ServiceProvider,
                includeStuckDetection: true
            );
            processed.Should().Be(1);
        });

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status
                .CompletedAt.Should()
                .NotBeNull("stuck handler should have been re-processed and completed");
        });
    }

    [Test]
    public async Task Inbox_OutboxSameDbContext_SkipsOutboxAndCreatesInboxEntriesDirectly()
    {
        // Arrange: same-DbContext optimization — OutboxTriggerInterceptor writes inbox entries directly
        // in the outbox transaction, bypassing OutboxProcessor and EfCoreMessageSender entirely.
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "inbox-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "inbox-events",
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("inbox-handler"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                {
                    d.UseInbox();
                    d.UseOutbox();
                });
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) =>
                {
                    opts.UseNpgsql(PostgresConnectionString);
                    opts.RegisterOutbox<TestDbContext>(sp);
                }
            );
        });

        await InitializeDatabase();

        // Stage via outbox (simulates transactional publish alongside business data)
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.OutboxMessages.Add(new TestEvent { Id = "outbox-inbox-1", Data = "end-to-end" });
            await db.SaveChangesAsync();
        });

        // Wait for InboxProcessor to pick up the directly-created entries and complete the handler
        await WaitForConditionAsync(
            async () =>
                await InScopeAsync(async ctx =>
                {
                    var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                    var status = await db.Set<InboxHandlerStatusEntity>()
                        .SingleOrDefaultAsync(s => s.HandlerKey == "inbox-handler");
                    return status?.CompletedAt != null;
                }),
            TimeSpan.FromSeconds(20)
        );

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            // Same-DbContext optimization: no outbox entry created for efcore transport
            var outboxMessages = await db.Set<OutboxMessageEntity>().ToListAsync();
            outboxMessages
                .Should()
                .BeEmpty("same-DbContext optimization skips outbox for efcore transport");

            var inboxMsg = await db.Set<InboxMessageEntity>().SingleAsync();
            inboxMsg.TransportName.Should().Be("efcore");

            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.HandlerKey.Should().Be("inbox-handler");
            status.CompletedAt.Should().NotBeNull();
            status.ErrorCount.Should().Be(0);
        });
    }
}
