using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Inbox;

public class TestBatchHandler : IBatchMessageHandler<TestEvent>
{
    public static List<IReadOnlyList<TestEvent>> ReceivedBatches { get; } = new();

    public Task HandleAsync(IReadOnlyList<TestEvent> messages, CancellationToken cancellationToken)
    {
        lock (ReceivedBatches)
        {
            ReceivedBatches.Add(messages.ToList());
        }
        return Task.CompletedTask;
    }
}

public class TestConsumedBatchHandler : IBatchMessageHandler<ConsumedMessage<TestEvent>>
{
    public static List<IReadOnlyList<ConsumedMessage<TestEvent>>> ReceivedBatches { get; } = new();

    public Task HandleAsync(
        IReadOnlyList<ConsumedMessage<TestEvent>> messages,
        CancellationToken cancellationToken
    )
    {
        lock (ReceivedBatches)
        {
            ReceivedBatches.Add(messages.ToList());
        }
        return Task.CompletedTask;
    }
}

public class FailingBatchHandler : IBatchMessageHandler<TestEvent>
{
    public static int CallCount;

    public Task HandleAsync(IReadOnlyList<TestEvent> messages, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref CallCount);
        throw new InvalidOperationException("Simulated batch failure");
    }
}

public class InboxBatchingTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : InboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Inbox_BatchHandler_ProcessesMessagesInBulk()
    {
        lock (TestBatchHandler.ReceivedBatches)
        {
            TestBatchHandler.ReceivedBatches.Clear();
        }

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
                        c.Consumes<TestEvent>(m =>
                                m.WithBatchHandler<TestBatchHandler>("batch-handler-a")
                            )
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

        // Publish 5 events into inbox
        for (var i = 0; i < 5; i++)
        {
            var indexStr = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await InScopeAsync(async ctx =>
            {
                var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                await bus.PublishDirectAsync(
                    new TestEvent { Id = $"batch-evt-{indexStr}", Data = $"payload-{indexStr}" },
                    new MessageProperties { Id = $"msg-batch-{indexStr}" }
                );
            });
        }

        await WaitForInboxEntriesAsync(5);

        // Process inbox manually
        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(ctx.ServiceProvider);
            processed.Should().Be(5);
        });

        // Verify batch handler received all 5 messages in a batch
        lock (TestBatchHandler.ReceivedBatches)
        {
            TestBatchHandler.ReceivedBatches.Should().HaveCount(1);
            TestBatchHandler.ReceivedBatches[0].Should().HaveCount(5);
            TestBatchHandler.ReceivedBatches[0].Select(e => e.Id).Should().Contain(["batch-evt-0", "batch-evt-1", "batch-evt-2", "batch-evt-3", "batch-evt-4"]);
        }

        // Verify database completed statuses
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(5);
            statuses.Should().AllSatisfy(s => s.CompletedAt.Should().NotBeNull());
        });
    }

    [Test]
    public async Task Inbox_BatchHandler_WithConsumedMessage_ReceivesMetadata()
    {
        lock (TestConsumedBatchHandler.ReceivedBatches)
        {
            TestConsumedBatchHandler.ReceivedBatches.Clear();
        }

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
                        c.Consumes<TestEvent>(m =>
                                m.WithBatchHandler<TestConsumedBatchHandler>("consumed-batch-handler")
                            )
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

        // Publish 3 events
        for (var i = 0; i < 3; i++)
        {
            var indexStr = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await InScopeAsync(async ctx =>
            {
                var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                await bus.PublishDirectAsync(
                    new TestEvent { Id = $"meta-evt-{indexStr}" },
                    new MessageProperties { Id = $"meta-msg-{indexStr}" }
                );
            });
        }

        await WaitForInboxEntriesAsync(3);

        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(ctx.ServiceProvider);
            processed.Should().Be(3);
        });

        lock (TestConsumedBatchHandler.ReceivedBatches)
        {
            TestConsumedBatchHandler.ReceivedBatches.Should().HaveCount(1);
            var batch = TestConsumedBatchHandler.ReceivedBatches[0];
            batch.Should().HaveCount(3);
            batch[0].Properties.Id.Should().Be("meta-msg-0");
            batch[0].Message.Id.Should().Be("meta-evt-0");
        }
    }

    [Test]
    public async Task Inbox_BatchHandler_FailsTransactionally_MarksAllStatusAsFailed()
    {
        FailingBatchHandler.CallCount = 0;

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
                        c.Consumes<TestEvent>(m =>
                                m.WithBatchHandler<FailingBatchHandler>("failing-batch-handler")
                            )
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

        // Publish 3 events
        for (var i = 0; i < 3; i++)
        {
            var indexStr = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await InScopeAsync(async ctx =>
            {
                var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                await bus.PublishDirectAsync(
                    new TestEvent { Id = $"fail-evt-{indexStr}" },
                    new MessageProperties { Id = $"fail-msg-{indexStr}" }
                );
            });
        }

        await WaitForInboxEntriesAsync(3);

        // Process inbox
        await InScopeAsync(async ctx =>
        {
            var processed = await ProcessInboxAsync(ctx.ServiceProvider);
            processed.Should().Be(3);
        });

        // Assert all statuses in batch incremented ErrorCount and recorded last error
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(3);
            statuses.Should().AllSatisfy(s =>
            {
                s.CompletedAt.Should().BeNull();
                s.ErrorCount.Should().Be(1);
                s.LastError.Should().Contain("Simulated batch failure");
            });
        });
    }
}
