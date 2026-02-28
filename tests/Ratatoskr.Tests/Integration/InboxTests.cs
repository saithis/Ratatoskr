using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>());
                bus.AddHandler<TestEvent, InboxHandlerA>("handler-a");
                bus.AddHandler<TestEvent, InboxHandlerB>("handler-b");
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
            await bus.PublishDirectAsync(new TestEvent { Id = "all-succeed-1", Data = "test" });
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
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>());
                bus.AddHandler<TestEvent, InboxHandlerA>("succeeding");
                bus.AddHandler<TestEvent, AlwaysFailingHandler>("failing");
            });

            AddInboxWithoutHostedService(services);

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = "isolation-1" });
        });

        // Act — first processing: succeeding completes, failing records an error
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        // Assert: succeeding completed, failing has ErrorCount=1
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(2);

            var succeeding = statuses.Single(s => s.HandlerKey == "succeeding");
            succeeding.CompletedAt.Should().NotBeNull("succeeding handler should have completed");
            succeeding.ErrorCount.Should().Be(0);

            var failing = statuses.Single(s => s.HandlerKey == "failing");
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

            var succeeding = statuses.Single(s => s.HandlerKey == "succeeding");
            succeeding.CompletedAt.Should().NotBeNull("succeeding should remain completed");
            succeeding.ErrorCount.Should().Be(0, "succeeding should not have been retried");

            var failing = statuses.Single(s => s.HandlerKey == "failing");
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
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>());
                bus.AddHandler<TestEvent, AlwaysFailingHandler>("failing");
            });

            AddInboxWithoutHostedService(services, opts => opts.MaxRetries = 10);

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = "backoff-1" });
        });

        // Attempt 1: ErrorCount=1, NextAttemptAt = startTime + 2^1 = startTime + 2s
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.ErrorCount.Should().Be(1);
            status.NextAttemptAt.Should().NotBeNull();
            status.NextAttemptAt!.Value.Should().BeCloseTo(startTime.AddSeconds(2), TimeSpan.FromMilliseconds(500));
        });

        // Processing immediately: nothing processed (NextAttemptAt is still in the future)
        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(ctx.ServiceProvider);
            processed.Should().Be(0);
        });

        // Advance 3s past the first retry window
        fakeTime.Advance(TimeSpan.FromSeconds(3));

        // Attempt 2: ErrorCount=2, NextAttemptAt = (startTime + 3s) + 2^2 = startTime + 7s
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.ErrorCount.Should().Be(2);
            // 2^2 = 4 seconds from now (which is startTime + 3s)
            status.NextAttemptAt!.Value.Should().BeCloseTo(startTime.AddSeconds(7), TimeSpan.FromMilliseconds(500));
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
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>());
                bus.AddHandler<TestEvent, AlwaysFailingHandler>("failing");
            });

            AddInboxWithoutHostedService(services, opts => opts.MaxRetries = 3);

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = "poison-1" });
        });

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
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>());
                bus.AddHandler<TestEvent, CountingHandler>("counting");
                bus.UseEfCoreInbox<TestDbContext>();
            });

            services.AddSingleton(callCounter);

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        const string messageId = "dedup-1";

        // Act — publish the SAME message ID twice (simulates duplicate delivery)
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = messageId, Data = "first delivery" });
        });

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = messageId, Data = "duplicate delivery" });
        });

        // Wait for the handler status to be completed
        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var status = await db.Set<InboxHandlerStatusEntity>()
                    .SingleOrDefaultAsync(s => s.MessageId == messageId && s.HandlerKey == "counting");
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
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>());
                bus.AddHandler<TestEvent, InboxHandlerA>("handler-a");
            });

            AddInboxWithoutHostedService(services, opts =>
                opts.StuckMessageThreshold = TimeSpan.FromMinutes(5));

            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
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

            db.Set<InboxMessageEntity>().Add(
                InboxMessageEntity.Create(messageId, "local", body, props, timeProvider));

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
    public async Task Inbox_MixedHandlers_InboxAndNonInboxHandlersBothRun()
    {
        // Arrange
        var nonInboxHandler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>());
                bus.AddHandler<TestEvent, TestEventHandler>(nonInboxHandler); // Non-inbox (fire-and-forget)
                bus.AddHandler<TestEvent, InboxHandlerA>("inbox-handler");   // Inbox-managed
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
            await bus.PublishDirectAsync(new TestEvent { Id = "mixed-1", Data = "mixed handlers" });
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
        nonInboxHandler.HandledMessages.Should().ContainSingle(m => m.Id == "mixed-1");

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
    public async Task Inbox_OutboxToLocalTransport_EndToEndCrashSafe()
    {
        // Arrange: full pipeline — Outbox → DurableLocalSender → InboxDB → InboxProcessor → handler
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c.Consumes<TestEvent>());
                bus.AddHandler<TestEvent, InboxHandlerA>("inbox-handler");
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
                var status = await db.Set<InboxHandlerStatusEntity>()
                    .SingleOrDefaultAsync(s => s.MessageId == "outbox-inbox-1");
                return status?.CompletedAt != null;
            }),
            TimeSpan.FromSeconds(20));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            var outboxMsg = await db.Set<OutboxMessageEntity>().SingleAsync();
            outboxMsg.ProcessedAt.Should().NotBeNull("outbox message must be marked processed");

            var inboxMsg = await db.Set<InboxMessageEntity>().SingleAsync();
            inboxMsg.Id.Should().Be("outbox-inbox-1");
            inboxMsg.TransportName.Should().Be("local");

            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.HandlerKey.Should().Be("inbox-handler");
            status.CompletedAt.Should().NotBeNull();
            status.ErrorCount.Should().Be(0);
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
    /// Registers inbox components as singletons WITHOUT the <see cref="IHostedService"/> background service.
    /// Gives tests deterministic control over when inbox processing runs via <see cref="ProcessInboxAsync"/>.
    /// Must be called after <c>UseLocalTransport()</c> (so LocalMessageSender is registered to replace).
    /// </summary>
    private static void AddInboxWithoutHostedService(
        IServiceCollection services,
        Action<InboxOptions>? configureOptions = null)
    {
        var options = new InboxOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<InboxProcessor<TestDbContext>>();
        // Do NOT register as IHostedService — tests call ProcessInboxAsync manually.
        services.AddSingleton<IInboxInterceptor, InboxInterceptor<TestDbContext>>();

        // Replace LocalMessageSender with DurableLocalMessageSender so inbox entries are written
        // when the test calls PublishDirectAsync.
        services.RemoveAll<IMessageSender>();
        services.AddSingleton<IMessageSender, DurableLocalMessageSender<TestDbContext>>();
    }

    private async Task<int> ProcessInboxAsync(
        IServiceProvider serviceProvider,
        bool includeStuckDetection = false)
    {
        var dbContext = serviceProvider.GetRequiredService<TestDbContext>();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var handlerRegistry = serviceProvider.GetRequiredService<InboxHandlerRegistry>();
        var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        var options = serviceProvider.GetRequiredService<IOptions<InboxOptions>>();
        var observers = serviceProvider.GetServices<IMessageActivityObserver>();
        var messageSerializer = serviceProvider.GetRequiredService<IMessageSerializer>();

        var processor = new InboxMessageProcessor<TestDbContext>(
            dbContext,
            scopeFactory,
            handlerRegistry,
            timeProvider,
            options.Value,
            observers,
            messageSerializer,
            NullLogger<InboxMessageProcessor<TestDbContext>>.Instance);

        var total = 0;
        while (true)
        {
            var count = await processor.ProcessBatchAsync(includeStuckDetection, CancellationToken.None);
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
    /// Since the handler is scoped (registered via AddHandler with inbox key), a new instance
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

    /// <summary>Thread-safe invocation counter for deduplication tests.</summary>
    private class InvocationCounter
    {
        private int _count;
        public void Increment() => Interlocked.Increment(ref _count);
        public int Count => _count;
    }

    #endregion
}
