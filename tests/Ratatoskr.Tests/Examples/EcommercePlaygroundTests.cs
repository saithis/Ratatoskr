using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlaygroundMessages;
using PlaygroundMessages.Messages;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.Tests.Integration;

namespace Ratatoskr.Tests.Examples;

public class EcommercePlaygroundTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string EvtChannel  => $"ecommerce-events-{TestId}";
    private string CmdChannel  => $"ecommerce-cmds-{TestId}";
    private string InventoryQ  => $"inventory-{TestId}";

    // --- Test 1: both handlers on a single consume channel receive the same message ---

    [Test]
    public async Task OrderPlaced_TwoHandlersOnChannel_BothInvoked()
    {
        // Simulates OrderService publishing OrderPlaced and two independent handlers
        // (InventoryService-like and NotificationService-like) both receiving it.
        // In production these are separate services/queues; here they are separate handlers
        // on the same channel to stay within a single test host.
        var inventoryCounter  = new InventoryCounter();
        var notificationCounter = new NotificationCounter();

        await StartTestAsync(services =>
        {
            services.AddSingleton(inventoryCounter);
            services.AddSingleton(notificationCounter);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));

                bus.AddEventPublishChannel(EvtChannel, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<OrderPlaced>());

                bus.AddEventConsumeChannel(EvtChannel, c => c
                    .WithRabbitMq(r => r
                        .WithTopicExchange()
                        .WithQueueName(InventoryQ)
                        .WithTransientQueue()
                        .WithQueueType(QueueType.Classic))
                    .Consumes<OrderPlaced>(m => m
                        .WithHandler<InventoryOrderPlacedHandler>(HandlerKeys.InventoryProcessOrder)
                        .WithHandler<NotificationOrderPlacedHandler>(HandlerKeys.NotifyOrderPlaced)));
            });
        });

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new OrderPlaced { OrderId = "fan-out-1" });
        });

        await WaitForConditionAsync(
            () => inventoryCounter.Count >= 1 && notificationCounter.Count >= 1,
            TimeSpan.FromSeconds(15),
            "Both handlers should receive OrderPlaced");

        inventoryCounter.Count.Should().Be(1);
        notificationCounter.Count.Should().Be(1);
    }

    // --- Test 2: inbox deduplication — same message ID delivered twice → handler invoked once ---

    [Test]
    public async Task InboxDedup_DuplicateMessageId_HandlerInvokedOnce()
    {
        // Simulates InventoryService inbox dedup: if the same CloudEvents message ID is
        // delivered twice (e.g., via the Dashboard "Replay" button), the inbox constraint
        // ensures the handler runs exactly once.
        var counter = new InventoryCounter();

        await StartTestAsync(services =>
        {
            services.AddSingleton(counter);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());

                bus.AddCommandPublishChannel(CmdChannel, c => c
                    .WithRabbitMq(r => r.WithDirectExchange())
                    .Produces<ProcessOrderCommand>());

                bus.AddCommandConsumeChannel(CmdChannel, c => c
                    .WithRabbitMq(r => r
                        .WithDirectExchange()
                        .WithQueueName(InventoryQ)
                        .WithTransientQueue()
                        .WithQueueType(QueueType.Classic))
                    .Consumes<ProcessOrderCommand>(m =>
                        m.WithHandler<ProcessCommandHandler>(HandlerKeys.InventoryProcessOrder))
                    .UseInbox<TestDbContext>());
            });

            services.AddDbContext<TestDbContext>((_, opts) => opts.UseNpgsql(PostgresConnectionString));
        });
        await InitializeDatabase();

        const string sharedMsgId = "dedup-msg-1";

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new ProcessOrderCommand { OrderId = "order-1" },
                new MessageProperties { Id = sharedMsgId });
        });

        await WaitForConditionAsync(() => counter.Count >= 1, TimeSpan.FromSeconds(15), "First delivery not processed");

        // Publish the same message ID again — inbox should suppress the second invocation
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new ProcessOrderCommand { OrderId = "order-1" },
                new MessageProperties { Id = sharedMsgId });
        });

        // Wait enough time for a second delivery to potentially arrive
        await Task.Delay(TimeSpan.FromSeconds(3));

        counter.Count.Should().Be(1, "inbox should suppress duplicate message ID delivery");
    }

    // --- Test 3: transactional outbox — SaveChanges creates an outbox row ---

    [Test]
    public async Task Outbox_SaveChanges_CreatesOutboxRow()
    {
        // Verifies that the outbox interceptor converts OutboxMessages.Add() into a persisted
        // outbox row when SaveChangesAsync is called (the relay is disabled so we can inspect
        // the row before it is sent).
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseOutbox(o => o.WithoutBackgroundProcessing()));

                bus.AddEventPublishChannel(EvtChannel, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<OrderPlaced>());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(PostgresConnectionString);
                opts.RegisterOutbox<TestDbContext>(sp);
            });
        });
        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.OutboxMessages.Add(new OrderPlaced { OrderId = "outbox-order-1" });
            await db.SaveChangesAsync();
        });

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var rows = await db.Set<OutboxMessageEntity>().ToListAsync();
            rows.Should().HaveCountGreaterThanOrEqualTo(1, "outbox row should exist after SaveChanges");
        });
    }

    [Test]
    public async Task Outbox_OneSave_StagesOrderPlacedAndCommand_CreatesTwoOutboxRows()
    {
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseOutbox(o => o.WithoutBackgroundProcessing()));

                bus.AddEventPublishChannel(EvtChannel, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<OrderPlaced>());

                bus.AddCommandPublishChannel(CmdChannel, c => c
                    .WithRabbitMq(r => r.WithDirectExchange())
                    .Produces<ProcessOrderCommand>());
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(PostgresConnectionString);
                opts.RegisterOutbox<TestDbContext>(sp);
            });
        });
        await InitializeDatabase();

        var orderId = Guid.NewGuid();
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.OutboxMessages.Add(
                new OrderPlaced { OrderId = orderId.ToString() },
                new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(orderId) });
            db.OutboxMessages.Add(
                new ProcessOrderCommand { OrderId = orderId.ToString() },
                new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(orderId) });
            await db.SaveChangesAsync();
        });

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var rows = await db.Set<OutboxMessageEntity>().ToListAsync();
            rows.Should().HaveCount(2, "each staged message type should produce one outbox row");
        });
    }

    // --- Test 4: failure mode — handler that always throws causes inbox handler to be poisoned ---

    [Test]
    public async Task InboxPoison_HandlerAlwaysFails_StatusBecomesPoisoned()
    {
        // Simulates InventoryService failure mode:
        // a handler that always throws exhausts retries and the inbox handler status is poisoned.
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(i => i.WithoutBackgroundProcessing()));

                bus.AddCommandPublishChannel(CmdChannel, c => c
                    .WithRabbitMq(r => r.WithDirectExchange())
                    .Produces<ProcessOrderCommand>());

                bus.AddCommandConsumeChannel(CmdChannel, c => c
                    .WithRabbitMq(r => r
                        .WithDirectExchange()
                        .WithQueueName(InventoryQ)
                        .WithTransientQueue()
                        .WithQueueType(QueueType.Classic))
                    .Consumes<ProcessOrderCommand>(m =>
                        m.WithHandler<AlwaysFailingCommandHandler>(HandlerKeys.InventoryProcessOrder))
                    .UseInbox<TestDbContext>());
            });

            services.AddDbContext<TestDbContext>((_, opts) => opts.UseNpgsql(PostgresConnectionString));
        });
        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new ProcessOrderCommand { OrderId = "failing-order" });
        });

        await WaitForConditionAsync(
            async () => await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                return await db.Set<InboxHandlerStatusEntity>().AnyAsync();
            }),
            TimeSpan.FromSeconds(10),
            "Inbox handler status row should appear");

        // Drive the inbox processor through enough cycles to exhaust retries
        for (var i = 0; i < 10; i++)
        {
            await InScopeAsync(async ctx =>
            {
                var processor = ctx.ServiceProvider.GetRequiredService<InboxMessageProcessor<TestDbContext>>();
                await processor.ProcessBatchAsync(includeStuckMessageDetection: true, CancellationToken.None);
            });
        }

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var poisoned = await db.Set<InboxHandlerStatusEntity>()
                .Where(s => s.IsPoisoned)
                .ToListAsync();
            poisoned.Should().HaveCountGreaterThanOrEqualTo(1,
                "handler should be poisoned after exhausting retries");
        });
    }

    // --- Counter types (distinct to allow separate DI registrations) ---

    private sealed class InventoryCounter
    {
        private int _count;
        public int Count => _count;
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class NotificationCounter
    {
        private int _count;
        public int Count => _count;
        public void Increment() => Interlocked.Increment(ref _count);
    }

    // --- Handler implementations ---

    private sealed class InventoryOrderPlacedHandler(InventoryCounter counter) : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced message, MessageProperties props, CancellationToken ct)
        {
            counter.Increment();
            return Task.CompletedTask;
        }
    }

    private sealed class NotificationOrderPlacedHandler(NotificationCounter counter) : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced message, MessageProperties props, CancellationToken ct)
        {
            counter.Increment();
            return Task.CompletedTask;
        }
    }

    private sealed class ProcessCommandHandler(InventoryCounter counter) : IMessageHandler<ProcessOrderCommand>
    {
        public Task HandleAsync(ProcessOrderCommand message, MessageProperties props, CancellationToken ct)
        {
            counter.Increment();
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysFailingCommandHandler : IMessageHandler<ProcessOrderCommand>
    {
        public Task HandleAsync(ProcessOrderCommand message, MessageProperties props, CancellationToken ct)
            => throw new InvalidOperationException("Simulated failure mode — handler always fails");
    }
}
