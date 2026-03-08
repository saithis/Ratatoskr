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

public class InboxCancellationAndTimeoutTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : InboxTestBase(rabbitMq, postgres)
{
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
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<CancellableHandler>("cancellable"))
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
    public async Task Inbox_CancellationDuringHandler_DoesNotIncrementErrorCount()
    {
        var coordination = new CancellableHandlerCoordination();
        var cts = new CancellationTokenSource();

        await StartTestAsync(services =>
        {
            services.AddSingleton(coordination);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<CancellableHandler>("cancellable"))
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
                new TestEvent { Id = "business-cancel-1" },
                new MessageProperties { Id = "cancel-1" });
        });

        await WaitForInboxEntriesAsync(1);

        var processTask = InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<InboxMessageProcessor<TestDbContext>>();
            await processor.ProcessBatchAsync(false, cts.Token);
        });

        await coordination.HandlerStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await processTask;

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.ErrorCount.Should().Be(0, "cancellation should not count as a handler failure");
            status.IsPoisoned.Should().BeFalse("cancellation should not poison the handler");
            status.CompletedAt.Should().BeNull("handler was interrupted");
            status.ProcessingStartedAt.Should().NotBeNull("status should remain in processing state for stuck detection");
        });
    }

    [Test]
    public async Task Inbox_HandlerTimeout_IncreasesErrorCount()
    {
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<SlowHandler>("slow-handler"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox =>
                    {
                        inbox.WithHandlerTimeout(TimeSpan.FromMilliseconds(100));
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
                new TestEvent { Id = "business-timeout-1" },
                new MessageProperties { Id = "timeout-1" });
        });

        await WaitForInboxEntriesAsync(1);

        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

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
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<SlowHandler>("slow-handler"))
                    .UseInbox<TestDbContext>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox =>
                    {
                        inbox.WithHandlerTimeout(TimeSpan.FromMilliseconds(100));
                        inbox.WithMaxRetries(2);
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
                new TestEvent { Id = "business-timeout-poison-1" },
                new MessageProperties { Id = "timeout-poison-1" });
        });

        await WaitForInboxEntriesAsync(1);

        for (int i = 0; i < 2; i++)
        {
            await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));
            fakeTime.Advance(TimeSpan.FromMinutes(10));
        }

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.IsPoisoned.Should().BeTrue();
            status.ErrorCount.Should().Be(2);
            status.CompletedAt.Should().BeNull();
        });
    }

    private class CancellableHandlerCoordination
    {
        public SemaphoreSlim HandlerStarted { get; } = new(0, 1);
        public SemaphoreSlim HandlerGate { get; } = new(0, 1);
    }

    private class CancellableHandler(CancellableHandlerCoordination coordination) : IMessageHandler<TestEvent>
    {
        public async Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
        {
            coordination.HandlerStarted.Release();
            await coordination.HandlerGate.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
        }
    }

    private class SlowHandler : IMessageHandler<TestEvent>
    {
        public async Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
    }
}
