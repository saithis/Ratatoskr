using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Inbox;

public class InboxErrorHandlingTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : InboxTestBase(rabbitMq, postgres)
{
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
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m
                        .WithHandler<InboxHandlerA>("succeeding")
                        .WithHandler<AlwaysFailingHandler>("failing"))
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
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<AlwaysFailingHandler>("failing"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox =>
                    {
                        inbox.WithMaxRetries(10);
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
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<AlwaysFailingHandler>("failing"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox =>
                    {
                        inbox.WithMaxRetries(3);
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
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<FailsThenSucceedsHandler>("fails-then-succeeds"))
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
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<AlwaysFailingHandler>("failing"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox =>
                    {
                        inbox.WithMaxRetries(20);
                        inbox.WithMaxRetryDelay(TimeSpan.FromSeconds(30));
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
    public async Task Inbox_MaxRetriesOne_PoisonedOnFirstFailure()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<AlwaysFailingHandler>("fail-once"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox =>
                    {
                        inbox.WithMaxRetries(1);
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
    public async Task Inbox_ErrorTruncation_LongErrorMessageTruncatedTo2000Chars()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<LongErrorHandler>("long-error"))
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
    public async Task Inbox_ErrorCountPreservedAfterSuccessfulRetry()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var counter = new InvocationCounter();

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddSingleton(counter);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel("inbox-events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<FailsThenSucceedsHandler>("flaky"))
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
                new TestEvent { Id = "business-errorcount-1" },
                new MessageProperties { Id = "errorcount-1" });
        });

        await WaitForInboxEntriesAsync(1);

        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));
        fakeTime.Advance(TimeSpan.FromSeconds(10));
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

    private class LongErrorHandler : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
            => throw new InvalidOperationException(new string('X', 5000));
    }
}
