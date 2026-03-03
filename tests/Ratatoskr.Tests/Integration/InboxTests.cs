using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Local;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Integration;

public class InboxTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    #region Tests

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
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.AddHandler<TestEvent, InboxHandlerB>();
                bus.UseEfCoreInbox<TestDbContext>();
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
            statuses.Should().Contain(s => s.HandlerKey == typeof(InboxHandlerA).FullName);
            statuses.Should().Contain(s => s.HandlerKey == typeof(InboxHandlerB).FullName);
            statuses.Should().AllSatisfy(s =>
            {
                s.CompletedAt.Should().NotBeNull();
                s.IsPoisoned.Should().BeFalse();
                s.ErrorCount.Should().Be(0);
            });
        });
    }

    [Test]
    public async Task Inbox_PerHandlerIsolation_FailedHandlerRetriedSuccessfulHandlerNot()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.AddHandler<TestEvent, AlwaysFailingHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox => inbox.WithoutBackgroundProcessing());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-isolation-1" },
                new MessageProperties { Id = "isolation-1" });
        });

        await WaitForInboxEntriesAsync(2);

        // Act — first processing: succeeding completes, failing records an error
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        // Assert: succeeding completed, failing has ErrorCount=1
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(2);

            var succeeding = statuses.Single(s => s.HandlerKey == typeof(InboxHandlerA).FullName);
            succeeding.CompletedAt.Should().NotBeNull("succeeding handler should have completed");
            succeeding.ErrorCount.Should().Be(0);

            var failing = statuses.Single(s => s.HandlerKey == typeof(AlwaysFailingHandler).FullName);
            failing.CompletedAt.Should().BeNull();
            failing.ErrorCount.Should().Be(1);
            failing.NextAttemptAt.Should().NotBeNull("should have a retry scheduled");
        });

        // Advance past the retry backoff window
        fakeTime.Advance(TimeSpan.FromSeconds(5));

        // Act — second processing: only failing is retried
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        // Assert: succeeding is STILL completed (not retried), failing has ErrorCount=2
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();

            var succeeding = statuses.Single(s => s.HandlerKey == typeof(InboxHandlerA).FullName);
            succeeding.CompletedAt.Should().NotBeNull("succeeding should remain completed");
            succeeding.ErrorCount.Should().Be(0, "succeeding should not have been retried");

            var failing = statuses.Single(s => s.HandlerKey == typeof(AlwaysFailingHandler).FullName);
            failing.ErrorCount.Should().Be(2, "failing handler should have been retried once more");
        });
    }

    [Test]
    public async Task Inbox_ExponentialBackoff_NextAttemptSetCorrectly()
    {
        // Arrange
        var startTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(startTime);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, AlwaysFailingHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox =>
                {
                    inbox.WithMaxRetries(10);
                    inbox.WithoutBackgroundProcessing();
                });
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-backoff-1" },
                new MessageProperties { Id = "backoff-1" });
        });

        await WaitForInboxEntriesAsync(1);

        // Attempt 1: ErrorCount=1, base = 2^1 = 2s, jitter range = [1s, 2s)
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.ErrorCount.Should().Be(1);
            status.NextAttemptAt.Should().NotBeNull();
            // With equal jitter: delay ∈ [base*0.5, base) = [1s, 2s)
            status.NextAttemptAt!.Value.Should().BeOnOrAfter(startTime.AddSeconds(1));
            status.NextAttemptAt!.Value.Should().BeOnOrBefore(startTime.AddSeconds(2));
        });

        // Processing immediately: nothing processed (NextAttemptAt is still in the future — at least 1s from now)
        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(ctx.ServiceProvider);
            processed.Should().Be(0);
        });

        // Advance 3s past the maximum possible first retry window
        fakeTime.Advance(TimeSpan.FromSeconds(3));

        // Attempt 2: ErrorCount=2, base = 2^2 = 4s, jitter range = [2s, 4s)
        // now = startTime + 3s → NextAttemptAt ∈ [startTime+5s, startTime+7s)
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.ErrorCount.Should().Be(2);
            status.NextAttemptAt!.Value.Should().BeOnOrAfter(startTime.AddSeconds(5));
            status.NextAttemptAt!.Value.Should().BeOnOrBefore(startTime.AddSeconds(7));
        });

        // Processing immediately: nothing processed (NextAttemptAt still in the future)
        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(ctx.ServiceProvider);
            processed.Should().Be(0);
        });
    }

    [Test]
    public async Task Inbox_MaxRetries_HandlerMarkedAsPoisoned()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, AlwaysFailingHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox =>
                {
                    inbox.WithMaxRetries(3);
                    inbox.WithoutBackgroundProcessing();
                });
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-poison-1" },
                new MessageProperties { Id = "poison-1" });
        });

        await WaitForInboxEntriesAsync(1);

        // Process MaxRetries times with time advances between each attempt
        for (int i = 0; i < 3; i++)
        {
            await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));
            fakeTime.Advance(TimeSpan.FromMinutes(10));
        }

        // Assert: handler is poisoned after MaxRetries
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.IsPoisoned.Should().BeTrue();
            status.ErrorCount.Should().Be(3);
            status.CompletedAt.Should().BeNull();
            status.NextAttemptAt.Should().BeNull("no more retries scheduled for poisoned handlers");
        });

        // Additional processing should not pick up the poisoned handler
        fakeTime.Advance(TimeSpan.FromHours(1));
        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(ctx.ServiceProvider);
            processed.Should().Be(0);
        });
    }

    [Test]
    public async Task Inbox_Deduplication_SameMessageIdAndHandlerKey_ProcessedOnce()
    {
        // Arrange
        var callCounter = new InvocationCounter();

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, CountingHandler>();
                bus.UseEfCoreInbox<TestDbContext>();
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

        // Wait for the handler status to be completed
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var status = await db.Set<InboxHandlerStatusEntity>()
                    .SingleOrDefaultAsync(s => s.MessageId == messageId);
                return status?.CompletedAt != null;
            }),
            TimeSpan.FromSeconds(15));

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
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.UseEfCoreInbox<TestDbContext>(inbox =>
                {
                    inbox.WithStuckMessageThreshold(TimeSpan.FromMinutes(5));
                    inbox.WithoutBackgroundProcessing();
                });
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Insert an inbox message and a handler status that simulates a mid-processing crash:
        // ProcessingStartedAt is set to NOW, simulating a handler that started but never completed.
        const string messageId = "stuck-1";
        var handlerKey = typeof(InboxHandlerA).FullName!;

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var timeProvider = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var serializer = ctx.ServiceProvider.GetRequiredService<IMessageSerializer>();

            var testEvent = new TestEvent { Id = messageId, Data = "stuck message" };
            var body = serializer.Serialize(testEvent);
            var props = new MessageProperties { Id = messageId, Type = "test.event" };

            db.Set<InboxMessageEntity>().Add(
                InboxMessageEntity.Create(messageId, "inbox-events", "local", body, props, timeProvider));

            var status = InboxHandlerStatusEntity.Create(messageId, handlerKey, timeProvider);
            status.MarkAsProcessing(timeProvider); // Simulate in-progress at startTime
            db.Set<InboxHandlerStatusEntity>().Add(status);

            await db.SaveChangesAsync();
        });

        // Advance 1 minute: status is "stuck" for 1 minute — below the 5-minute threshold
        fakeTime.Advance(TimeSpan.FromMinutes(1));

        // Processing with stuck detection: too recent, should NOT be picked up
        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(ctx.ServiceProvider, includeStuckDetection: true);
            processed.Should().Be(0, "handler started only 1 minute ago — not yet considered stuck");
        });

        // Advance to 6 minutes since ProcessingStartedAt — past the 5-minute threshold
        fakeTime.Advance(TimeSpan.FromMinutes(5));

        // Processing with stuck detection: old enough, should be picked up and completed
        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(ctx.ServiceProvider, includeStuckDetection: true);
            processed.Should().Be(1);
        });

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.CompletedAt.Should().NotBeNull("stuck handler should have been re-processed and completed");
        });
    }

    [Test]
    public async Task Inbox_MixedChannel_InboxAndNonInboxMessageTypes_BothWorkCorrectly()
    {
        // Arrange: same channel has two message types — TestEvent uses inbox, OrderCreatedEvent does not
        var nonInboxHandler = new OrderCreatedTrackingHandler();

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c
                    .WithLocal()
                    .Produces<TestEvent>()
                    .Produces<OrderCreatedEvent>());
                bus.AddEventConsumeChannel("inbox-events", c =>
                {
                    c.Consumes<TestEvent>(m => m.UseInbox()); // inbox-managed
                    c.Consumes<OrderCreatedEvent>();           // fire-and-forget
                });
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.AddHandler<OrderCreatedEvent, OrderCreatedTrackingHandler>(nonInboxHandler);
                bus.UseEfCoreInbox<TestDbContext>();
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Act — publish both message types
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-mixed-1", Data = "inbox managed" },
                new MessageProperties { Id = "mixed-1" });
            await bus.PublishDirectAsync(
                new OrderCreatedEvent { OrderId = Guid.NewGuid(), Amount = 42.00m },
                new MessageProperties { Id = "mixed-order-1" });
        });

        // Wait for the inbox handler status to be completed
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var status = await db.Set<InboxHandlerStatusEntity>().SingleOrDefaultAsync();
                return status?.CompletedAt != null;
            }),
            TimeSpan.FromSeconds(15));

        // Non-inbox handler should have been called directly via MessageDispatcher
        await WaitForConditionAsync(
            () => Task.FromResult(nonInboxHandler.HandledMessages.Any()),
            TimeSpan.FromSeconds(5));
        nonInboxHandler.HandledMessages.Should().HaveCount(1);

        // Inbox handler should have been completed via InboxProcessor
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.HandlerKey.Should().Be(typeof(InboxHandlerA).FullName);
            status.CompletedAt.Should().NotBeNull();
            status.ErrorCount.Should().Be(0);
        });
    }

    [Test]
    public async Task Inbox_OutboxToLocalTransport_EndToEndCrashSafe()
    {
        // Arrange: full pipeline — Outbox → DurableLocalSender → InboxDB → InboxProcessor → handler
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.AddEfCoreOutbox<TestDbContext>();
                bus.UseEfCoreInbox<TestDbContext>();
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(PostgresConnectionString);
                opts.RegisterOutbox<TestDbContext>(sp);
            });
        });

        await InitializeDatabase();

        // Stage via outbox (simulates transactional publish alongside business data)
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.OutboxMessages.Add(new TestEvent { Id = "outbox-inbox-1", Data = "end-to-end" });
            await db.SaveChangesAsync();
        });

        // Wait for the full pipeline: OutboxProcessor → DurableLocalSender → InboxProcessor → CompletedAt
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var status = await db.Set<InboxHandlerStatusEntity>().SingleOrDefaultAsync();
                return status?.CompletedAt != null;
            }),
            TimeSpan.FromSeconds(20));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            var outboxMsg = await db.Set<OutboxMessageEntity>().SingleAsync();
            outboxMsg.ProcessedAt.Should().NotBeNull("outbox message must be marked processed");

            var inboxMsg = await db.Set<InboxMessageEntity>().SingleAsync();
            inboxMsg.TransportName.Should().Be("local");

            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.HandlerKey.Should().Be(typeof(InboxHandlerA).FullName);
            status.CompletedAt.Should().NotBeNull();
            status.ErrorCount.Should().Be(0);
        });
    }

    [Test]
    public async Task Inbox_AllHandlersAutoEnrolled_WhenMessageUsesInbox()
    {
        // Arrange: UseInbox() on message — ALL handlers for that message type
        // are automatically enrolled in the inbox (no per-handler config needed)
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.AddHandler<TestEvent, InboxHandlerB>();
                bus.UseEfCoreInbox<TestDbContext>();
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-auto-enroll-1" },
                new MessageProperties { Id = "auto-enroll-1" });
        });

        // Both handlers should be in the inbox (enrolled automatically via UseInbox on message)
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
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(2);
            // Keys should be the handler CLR full names (auto-generated)
            statuses.Should().Contain(s => s.HandlerKey == typeof(InboxHandlerA).FullName);
            statuses.Should().Contain(s => s.HandlerKey == typeof(InboxHandlerB).FullName);
        });
    }

    [Test]
    public async Task Inbox_OrderIndependent_UseEfCoreInboxBeforeUseLocalTransport()
    {
        // Verifies that UseEfCoreInbox can be called BEFORE UseLocalTransport
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                // Intentionally call UseEfCoreInbox FIRST
                bus.UseEfCoreInbox<TestDbContext>();
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-order-1" },
                new MessageProperties { Id = "order-1" });
        });

        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var status = await db.Set<InboxHandlerStatusEntity>().SingleOrDefaultAsync();
                return status?.CompletedAt != null;
            }),
            TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task Inbox_HandlerSucceedsOnRetry_MarkedAsCompleted()
    {
        // Arrange: handler that fails twice, then succeeds on attempt 3
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var counter = new InvocationCounter();

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddSingleton(counter);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, FailsThenSucceedsHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox =>
                {
                    inbox.WithMaxRetries(5);
                    inbox.WithoutBackgroundProcessing();
                });
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-retry-succeed-1" },
                new MessageProperties { Id = "retry-succeed-1" });
        });

        await WaitForInboxEntriesAsync(1);

        // Attempt 1: fails (ErrorCount=1)
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));
        fakeTime.Advance(TimeSpan.FromMinutes(1));

        // Attempt 2: fails (ErrorCount=2)
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));
        fakeTime.Advance(TimeSpan.FromMinutes(1));

        // Attempt 3: succeeds
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        // Assert
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.CompletedAt.Should().NotBeNull("handler should have succeeded on attempt 3");
            status.ErrorCount.Should().Be(2, "two failures before success");
            status.IsPoisoned.Should().BeFalse();
        });

        counter.Count.Should().Be(3, "handler invoked 3 times total (2 failures + 1 success)");
    }

    [Test]
    public async Task Inbox_MaxRetryDelayCap_BackoffDoesNotExceedMax()
    {
        // Arrange
        var startTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(startTime);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, AlwaysFailingHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox =>
                {
                    inbox.WithMaxRetries(20);
                    inbox.WithMaxRetryDelay(TimeSpan.FromSeconds(30));
                    inbox.WithoutBackgroundProcessing();
                });
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-cap-1" },
                new MessageProperties { Id = "cap-1" });
        });

        await WaitForInboxEntriesAsync(1);

        // Process 10 times (2^10 = 1024s without cap, but cap is 30s)
        for (int i = 0; i < 10; i++)
        {
            await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));
            fakeTime.Advance(TimeSpan.FromMinutes(5));
        }

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.ErrorCount.Should().Be(10);
            status.IsPoisoned.Should().BeFalse();
            // NextAttemptAt should be at most 30 seconds from "now"
            var now = fakeTime.GetUtcNow();
            (status.NextAttemptAt!.Value - now).TotalSeconds.Should().BeLessThanOrEqualTo(30,
                "backoff should be capped at MaxRetryDelay");
        });
    }

    [Test]
    public async Task Inbox_ValidationFailure_UseInboxWithoutHandlers_ThrowsAtStartup()
    {
        // Arrange & Act & Assert: message with UseInbox() but no handlers should throw at startup
        var act = async () =>
        {
            await StartTestAsync(services =>
            {
                services.AddRatatoskr(bus =>
                {
                    bus.UseLocalTransport();
                    bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                    // No handlers registered for TestEvent!
                    bus.UseEfCoreInbox<TestDbContext>();
                });

                services.AddDbContext<TestDbContext>((sp, opts) =>
                    opts.UseNpgsql(PostgresConnectionString));
            });
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no handlers are registered*");
    }

    [Test]
    public async Task Inbox_ErrorTruncation_LongErrorMessageTruncatedTo2000Chars()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, LongErrorHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox => inbox.WithoutBackgroundProcessing());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-long-error-1" },
                new MessageProperties { Id = "long-error-1" });
        });

        await WaitForInboxEntriesAsync(1);

        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.ErrorCount.Should().Be(1);
            status.LastError.Length.Should().BeLessThanOrEqualTo(2000,
                "error message should be truncated to max column length");
        });
    }

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
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.UseEfCoreInbox<TestDbContext>(inbox => inbox.WithoutBackgroundProcessing());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Publish 5 messages while processing is disabled
        for (int i = 0; i < 5; i++)
        {
            await InScopeAsync(async ctx =>
            {
                var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                await bus.PublishDirectAsync(
                    new TestEvent { Id = $"business-accum-{i}" },
                    new MessageProperties { Id = $"accum-{i}" });
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
                bus.AddEventConsumeChannel("inbox-events", c =>
                {
                    c.Consumes<TestEvent>(m => m.UseInbox());
                    c.Consumes<OrderCreatedEvent>(m => m.UseInbox());
                });
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.AddHandler<OrderCreatedEvent, OrderCreatedInboxHandler>();
                bus.UseEfCoreInbox<TestDbContext>();
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
            statuses.Should().Contain(s => s.HandlerKey == typeof(InboxHandlerA).FullName && s.MessageId == "multi-test-1");
            statuses.Should().Contain(s => s.HandlerKey == typeof(OrderCreatedInboxHandler).FullName && s.MessageId == "multi-order-1");

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
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.UseEfCoreInbox<TestDbContext>(inbox => inbox.WithoutBackgroundProcessing());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        const string cloudEventsId = "ce-roundtrip-1";
        const string businessId = "business-roundtrip-1";
        const string data = "special chars: <>&\"' \u00e9\u00e8\u00ea";

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
    public async Task Inbox_ConcurrentDuplicate_ExactlyOneMessageAndStatus()
    {
        // Arrange: publish the same message ID from two concurrent tasks
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.UseEfCoreInbox<TestDbContext>();
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        const string sharedMessageId = "concurrent-dedup-1";

        // Act: publish the same message ID concurrently from two tasks
        await Task.WhenAll(
            InScopeAsync(async ctx =>
            {
                var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                await bus.PublishDirectAsync(
                    new TestEvent { Id = "business-concurrent-1" },
                    new MessageProperties { Id = sharedMessageId });
            }),
            InScopeAsync(async ctx =>
            {
                var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                await bus.PublishDirectAsync(
                    new TestEvent { Id = "business-concurrent-2" },
                    new MessageProperties { Id = sharedMessageId });
            })
        );

        // Wait for handler to complete
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var status = await db.Set<InboxHandlerStatusEntity>()
                    .SingleOrDefaultAsync(s => s.MessageId == sharedMessageId);
                return status?.CompletedAt != null;
            }),
            TimeSpan.FromSeconds(15));

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
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, CountingHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox => inbox.WithoutBackgroundProcessing());
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

    [Test]
    public async Task Inbox_CancellationToken_PropagatedToHandler()
    {
        // Arrange: handler that blocks until a semaphore is released
        var coordination = new CancellableHandlerCoordination();

        await StartTestAsync(services =>
        {
            services.AddSingleton(coordination);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, CancellableHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox => inbox.WithoutBackgroundProcessing());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-cancel-1" },
                new MessageProperties { Id = "cancel-1" });
        });

        await WaitForInboxEntriesAsync(1);

        // Act: start processing with a cancellable token, then cancel mid-handler.
        // Call ProcessBatchAsync directly (not the looping helper) to avoid a second
        // iteration hitting the already-cancelled token on the initial DB query.
        using var cts = new CancellationTokenSource();
        var processTask = InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<InboxMessageProcessor<TestDbContext>>();
            await processor.ProcessBatchAsync(false, cts.Token);
        });

        // Wait for handler to start, then cancel
        await coordination.HandlerStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        coordination.HandlerGate.Release(); // Unblock the handler so it can observe cancellation

        // ProcessBatchAsync catches OperationCanceledException internally and breaks gracefully
        await processTask;

        // Status should remain incomplete (not marked as completed or failed — stuck detection recovers it)
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.CompletedAt.Should().BeNull("handler was cancelled, not completed");
            status.ErrorCount.Should().Be(0, "cancellation should not count as a handler failure");
            status.IsPoisoned.Should().BeFalse("cancellation should not poison the handler");
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
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.UseEfCoreInbox<TestDbContext>(inbox =>
                {
                    inbox.WithBatchSize(3);
                    inbox.WithoutBackgroundProcessing();
                });
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Publish 5 messages
        for (int i = 0; i < 5; i++)
        {
            await InScopeAsync(async ctx =>
            {
                var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                await bus.PublishDirectAsync(
                    new TestEvent { Id = $"business-batch-{i}" },
                    new MessageProperties { Id = $"batch-{i}" });
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
    public async Task Inbox_ZeroInboxMessages_MessagesDispatchedNormally()
    {
        // Arrange: UseEfCoreInbox configured but no messages have UseInbox() — messages dispatched directly
        var nonInboxHandler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>()); // no UseInbox()
                bus.AddHandler<TestEvent, TestEventHandler>(nonInboxHandler);
                bus.UseEfCoreInbox<TestDbContext>(); // Inbox enabled, but no messages opted in
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
                new TestEvent { Id = "business-zero-inbox-1", Data = "no inbox messages" },
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
            inboxMessages.Should().BeEmpty("no inbox messages means no inbox rows");

            var handlerStatuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            handlerStatuses.Should().BeEmpty();
        });
    }

    [Test]
    public async Task Inbox_CancellationDuringHandler_DoesNotIncrementErrorCount()
    {
        // Verifies that when the cancellation token fires mid-handler (e.g. app shutdown),
        // the handler's ErrorCount is NOT incremented and the status is NOT poisoned.
        // Stuck detection will recover it on the next restart.

        var coordination = new CancellableHandlerCoordination();
        var cts = new CancellationTokenSource();

        await StartTestAsync(services =>
        {
            services.AddSingleton(coordination);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, CancellableHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox => inbox.WithoutBackgroundProcessing());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-cancel-1" },
                new MessageProperties { Id = "cancel-1" });
        });

        await WaitForInboxEntriesAsync(1);

        // Process with a cancellable token — cancel while handler is running
        var processTask = InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<InboxMessageProcessor<TestDbContext>>();
            await processor.ProcessBatchAsync(false, cts.Token);
        });

        // Wait for handler to start, then cancel
        await coordination.HandlerStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        // The handler will throw OperationCanceledException
        await processTask;

        // Assert: ErrorCount should still be 0 and status should NOT be poisoned
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.ErrorCount.Should().Be(0, "cancellation should not count as a handler failure");
            status.IsPoisoned.Should().BeFalse("cancellation should not poison the handler");
            status.CompletedAt.Should().BeNull("handler was interrupted");
            // ProcessingStartedAt should still be set (stuck detection picks it up)
            status.ProcessingStartedAt.Should().NotBeNull("status should remain in processing state for stuck detection");
        });
    }

    [Test]
    public async Task Inbox_MaxRetriesOne_PoisonedOnFirstFailure()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, AlwaysFailingHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox =>
                {
                    inbox.WithMaxRetries(1);
                    inbox.WithoutBackgroundProcessing();
                });
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-maxretries1-1" },
                new MessageProperties { Id = "maxretries1-1" });
        });

        await WaitForInboxEntriesAsync(1);

        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.ErrorCount.Should().Be(1);
            status.IsPoisoned.Should().BeTrue("should be poisoned after a single failure with MaxRetries=1");
        });
    }

    [Test]
    public async Task Inbox_PerHandlerSave_CompletedHandlersSurviveSubsequentFailures()
    {
        // Verifies that when handler A succeeds and handler B fails,
        // handler A's completed state is already persisted and won't be lost.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.AddHandler<TestEvent, AlwaysFailingHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox => inbox.WithoutBackgroundProcessing());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-persist-1" },
                new MessageProperties { Id = "persist-1" });
        });

        await WaitForInboxEntriesAsync(2);

        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        // Verify both states are persisted correctly
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var succeeding = await db.Set<InboxHandlerStatusEntity>()
                .SingleAsync(s => s.HandlerKey == typeof(InboxHandlerA).FullName);
            succeeding.CompletedAt.Should().NotBeNull();
            succeeding.ErrorCount.Should().Be(0);

            var failing = await db.Set<InboxHandlerStatusEntity>()
                .SingleAsync(s => s.HandlerKey == typeof(AlwaysFailingHandler).FullName);
            failing.CompletedAt.Should().BeNull();
            failing.ErrorCount.Should().Be(1);
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
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.UseEfCoreInbox<TestDbContext>(inbox => inbox.WithoutBackgroundProcessing());
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
        // When using the outbox with local transport, the OutboxTriggerInterceptor writes
        // inbox entries in the same DB transaction. InboxMessageEntity.Create validates
        // the ID length and throws synchronously during SaveChangesAsync.
        var tooLongId = new string('a', 201);

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.AddEfCoreOutbox<TestDbContext>();
                bus.UseEfCoreInbox<TestDbContext>();
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

    [Test]
    public async Task Inbox_ErrorCountPreservedAfterSuccessfulRetry()
    {
        // Handler fails twice, then succeeds on third attempt.
        // After success, ErrorCount should still be 2 (not reset).
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var counter = new InvocationCounter();

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddSingleton(counter);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, FailsThenSucceedsHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox => inbox.WithoutBackgroundProcessing());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-errorcount-1" },
                new MessageProperties { Id = "errorcount-1" });
        });

        await WaitForInboxEntriesAsync(1);

        // Attempt 1: fails
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        // Attempt 2: fails
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));
        fakeTime.Advance(TimeSpan.FromSeconds(10));
        // Attempt 3: succeeds
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.CompletedAt.Should().NotBeNull("should have completed on third attempt");
            status.ErrorCount.Should().Be(2, "error count should be preserved even after success");
            status.IsPoisoned.Should().BeFalse();
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
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.UseEfCoreInbox<TestDbContext>(inbox => inbox.WithoutBackgroundProcessing());
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

    [Test]
    public async Task Inbox_HandlerKeyNoLongerRegistered_PoisonedImmediately()
    {
        // Simulates a deployment where a handler is removed/renamed: the handler status row
        // references a key that no longer exists in InboxHandlerRegistry.
        // The processor should poison it immediately — no point retrying 5 times.

        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handlerKey = typeof(InboxHandlerA).FullName!;

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, InboxHandlerA>();
                bus.UseEfCoreInbox<TestDbContext>(inbox =>
                {
                    inbox.WithMaxRetries(5);
                    inbox.WithoutBackgroundProcessing();
                });
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Publish to create inbox entries
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-unrecoverable-1" },
                new MessageProperties { Id = "unrecoverable-1" });
        });

        await WaitForInboxEntriesAsync(1);

        // Simulate a deployment that removed/renamed the handler:
        // change the handler key in the DB to something that's no longer registered.
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                $"""UPDATE "InboxHandlerStatusEntity" SET "HandlerKey" = 'handler-removed-in-v2' WHERE "HandlerKey" = '{handlerKey}'""");
        });

        // Process — should poison immediately, NOT retry 5 times
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
    public async Task Inbox_HandlerScopeIsolation_HandlersDoNotShareDbContext()
    {
        // Verifies that MessageDispatcher creates a separate DI scope per handler,
        // so changes to scoped services (like DbContext) in one handler don't leak to another.
        var tracker = new ScopeIsolationTracker();
        await StartTestAsync(services =>
        {
            services.AddSingleton(tracker);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("test-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("test-events", c => c.Consumes<TestEvent>());
                // Two non-inbox handlers — each should get its own DI scope
                bus.AddHandler<TestEvent, ChangeTrackerPollutingHandler>();
                bus.AddHandler<TestEvent, ChangeTrackerCheckingHandler>();
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "scope-isolation-1" },
                new MessageProperties { Id = "scope-isolation-1" });
        });

        await tracker.WaitForCompletionAsync(TimeSpan.FromSeconds(10));

        tracker.CheckingHandlerSawChanges.Should().BeFalse(
            "handlers should have isolated DI scopes — ChangeTrackerCheckingHandler should not see ChangeTrackerPollutingHandler's tracked entities");
    }

    [Test]
    public async Task Inbox_InterceptorFailure_HandlersNotInvoked()
    {
        // Verifies that when the route interceptor fails, no handlers are invoked.
        var counter = new InvocationCounter();
        var failingInterceptor = new AlwaysFailingInterceptor();

        await StartTestAsync(services =>
        {
            services.AddSingleton(counter);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, CountingHandler>();
                bus.UseEfCoreInbox<TestDbContext>();
            });

            // Replace the real IMessageRouteInterceptor with one that always throws.
            services.RemoveAll<IMessageRouteInterceptor>();
            services.AddSingleton<IMessageRouteInterceptor>(failingInterceptor);

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // Act — publish a message. PublishDirectAsync succeeds (writes to in-memory channel),
        // but the consumer-side interceptor throws before dispatch.
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-accept-fail-1" },
                new MessageProperties { Id = "accept-fail-1" });
        });

        // Wait for the interceptor to be called (deterministic, no arbitrary delay)
        await failingInterceptor.WaitForCallAsync(TimeSpan.FromSeconds(5));

        // Assert — handlers should NOT have been called because the interceptor
        // threw before MessageRouter could proceed.
        counter.Count.Should().Be(0,
            "handlers must not execute when route interception fails");
    }

    [Test]
    public async Task Inbox_HandlerTimeout_IncreasesErrorCount()
    {
        // Verifies that a handler exceeding the configured timeout is treated as a failure.
        // The timeout CTS cancels only the handler's token; the outer cancellationToken is NOT cancelled,
        // so the OperationCanceledException falls into the general failure path (not the shutdown path).
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, SlowHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox =>
                {
                    inbox.WithHandlerTimeout(TimeSpan.FromMilliseconds(100));
                    inbox.WithoutBackgroundProcessing();
                });
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-timeout-1" },
                new MessageProperties { Id = "timeout-1" });
        });

        await WaitForInboxEntriesAsync(1);

        // Act — process; the handler will be cancelled by the timeout
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        // Assert
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.CompletedAt.Should().BeNull("handler timed out and should not be marked as completed");
            status.ErrorCount.Should().Be(1);
            status.IsPoisoned.Should().BeFalse();
        });
    }

    [Test]
    public async Task Inbox_HandlerTimeout_EventuallyPoisoned()
    {
        // Verifies that repeated timeouts eventually poison the handler status.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>(m => m.UseInbox()));
                bus.AddHandler<TestEvent, SlowHandler>();
                bus.UseEfCoreInbox<TestDbContext>(inbox =>
                {
                    inbox.WithHandlerTimeout(TimeSpan.FromMilliseconds(100));
                    inbox.WithMaxRetries(2);
                    inbox.WithoutBackgroundProcessing();
                });
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-timeout-poison-1" },
                new MessageProperties { Id = "timeout-poison-1" });
        });

        await WaitForInboxEntriesAsync(1);

        // Process MaxRetries times with time advances between each attempt
        for (int i = 0; i < 2; i++)
        {
            await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));
            fakeTime.Advance(TimeSpan.FromMinutes(10));
        }

        // Assert — handler should be poisoned after MaxRetries timeouts
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.IsPoisoned.Should().BeTrue();
            status.ErrorCount.Should().Be(2);
            status.CompletedAt.Should().BeNull();
        });
    }

    #endregion

    #region Helpers

    private async Task InitializeDatabase()
    {
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            await db.Database.EnsureCreatedAsync();
        });
    }

    /// <summary>
    /// Waits for the expected number of inbox handler status entries to appear in the database.
    /// With the new architecture, inbox entries are written by the consumer-side <see cref="InboxRouteInterceptor{TDbContext}"/>,
    /// which runs asynchronously after <c>PublishDirectAsync</c> writes to the in-memory channel.
    /// </summary>
    private async Task WaitForInboxEntriesAsync(int expectedCount, TimeSpan? timeout = null)
    {
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var count = await db.Set<InboxHandlerStatusEntity>().CountAsync();
                return count >= expectedCount;
            }),
            timeout ?? TimeSpan.FromSeconds(10),
            $"Expected {expectedCount} inbox handler status entries to appear within timeout");
    }

    private async Task<int> ProcessInboxAsync(
        IServiceProvider serviceProvider,
        bool includeStuckDetection = false,
        CancellationToken cancellationToken = default)
    {
        var token = cancellationToken == default ? CancellationToken.None : cancellationToken;
        var total = 0;
        while (true)
        {
            using var scope = serviceProvider.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<InboxMessageProcessor<TestDbContext>>();
            var count = await processor.ProcessBatchAsync(includeStuckDetection, token);
            total += count;
            if (count == 0) break;
        }
        return total;
    }

    #endregion

    #region Handler Types

    /// <summary>Basic handler that always succeeds.</summary>
    private class InboxHandlerA : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>Second basic handler that always succeeds (different type needed for two-handler tests).</summary>
    private class InboxHandlerB : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>Handler that always throws, used for retry/backoff/poison tests.</summary>
    private class AlwaysFailingHandler : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
            => throw new InvalidOperationException("Handler failed intentionally");
    }

    /// <summary>
    /// Handler that increments a singleton <see cref="InvocationCounter"/> on each call.
    /// Since the handler is scoped (registered via AddHandler), a new instance
    /// is created per InboxProcessor invocation, but the singleton counter persists.
    /// </summary>
    private class CountingHandler(InvocationCounter counter) : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
        {
            counter.Increment();
            return Task.CompletedTask;
        }
    }

    /// <summary>Thread-safe invocation counter for deduplication and retry tests.</summary>
    private class InvocationCounter
    {
        private int _count;
        public int Increment() => Interlocked.Increment(ref _count);
        public int Count => _count;
    }

    /// <summary>Handler that fails the first N invocations then succeeds.
    /// Since inbox handlers are scoped (new instance per invocation), the failure count
    /// must be tracked via the singleton <see cref="InvocationCounter"/>.</summary>
    private class FailsThenSucceedsHandler(InvocationCounter counter) : IMessageHandler<TestEvent>
    {
        private const int FailuresBeforeSuccess = 2;

        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
        {
            var attempt = counter.Increment();
            if (attempt <= FailuresBeforeSuccess)
                throw new InvalidOperationException($"Transient failure (attempt {attempt})");
            return Task.CompletedTask;
        }
    }

    /// <summary>Handler for OrderCreatedEvent (second message type for multi-type tests).</summary>
    private class OrderCreatedInboxHandler : IMessageHandler<OrderCreatedEvent>
    {
        public Task HandleAsync(OrderCreatedEvent message, MessageProperties props, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>Tracking handler for OrderCreatedEvent (non-inbox, stores handled messages).</summary>
    private class OrderCreatedTrackingHandler : IMessageHandler<OrderCreatedEvent>
    {
        public List<OrderCreatedEvent> HandledMessages { get; } = new();

        public Task HandleAsync(OrderCreatedEvent message, MessageProperties props, CancellationToken ct)
        {
            HandledMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>Handler that throws an exception with a message exceeding 2000 chars.</summary>
    private class LongErrorHandler : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
            => throw new InvalidOperationException(new string('X', 5000));
    }

    /// <summary>Coordination object for the cancellable handler test (avoids DI ambiguity with two SemaphoreSlim params).</summary>
    private class CancellableHandlerCoordination
    {
        public SemaphoreSlim HandlerStarted { get; } = new(0, 1);
        public SemaphoreSlim HandlerGate { get; } = new(0, 1);
    }

    /// <summary>Handler that blocks until a semaphore is released, then checks for cancellation.</summary>
    private class CancellableHandler(CancellableHandlerCoordination coordination) : IMessageHandler<TestEvent>
    {
        public async Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
        {
            coordination.HandlerStarted.Release();
            await coordination.HandlerGate.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Tracks scope isolation: adds an entity to the change tracker but doesn't save.</summary>
    private class ChangeTrackerPollutingHandler(TestDbContext db) : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
        {
            // Add entity to change tracker but DON'T save — if scopes leak, the next handler sees this
            db.TestEntities.Add(new TestEntity { Name = "leaked-from-polluting-handler" });
            return Task.CompletedTask;
        }
    }

    /// <summary>Checks whether the DbContext has any tracked changes (it shouldn't if scopes are isolated).</summary>
    private class ChangeTrackerCheckingHandler(TestDbContext db, ScopeIsolationTracker tracker) : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
        {
            tracker.CheckingHandlerSawChanges = db.ChangeTracker.HasChanges();
            tracker.SignalCompletion();
            return Task.CompletedTask;
        }
    }

    /// <summary>Coordination object for scope isolation tests.</summary>
    private class ScopeIsolationTracker
    {
        private readonly TaskCompletionSource _completed = new();
        public bool CheckingHandlerSawChanges { get; set; }

        public void SignalCompletion() => _completed.TrySetResult();
        public Task WaitForCompletionAsync(TimeSpan timeout) => _completed.Task.WaitAsync(timeout);
    }

    /// <summary>Handler that blocks indefinitely until its cancellation token fires. Used for timeout tests.</summary>
    private class SlowHandler : IMessageHandler<TestEvent>
    {
        public async Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    /// <summary>Interceptor that always throws, used to test route interception failure behavior.</summary>
    private class AlwaysFailingInterceptor : IMessageRouteInterceptor
    {
        private readonly TaskCompletionSource _called = new();

        public Task WaitForCallAsync(TimeSpan timeout) => _called.Task.WaitAsync(timeout);

        public Task<RouteInterceptResult> BeforeDispatchAsync(byte[] body, MessageProperties properties,
            string transportName, string channelName, CancellationToken cancellationToken)
        {
            _called.TrySetResult();
            throw new InvalidOperationException("Simulated interceptor failure");
        }
    }

    #endregion
}
