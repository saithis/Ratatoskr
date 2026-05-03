using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.RabbitMq;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.Tests.Integration;
using Ratatoskr.Tests.Integration.Outbox;

namespace Ratatoskr.Tests.Examples;

public class EcommercePlaygroundTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : OutboxTestBase(rabbitMq, postgres)
{
    private string EvtChannel  => $"ecommerce-events-{TestId}";
    private string CmdChannel  => $"ecommerce-cmds-{TestId}";
    private string InventoryQ  => $"inventory-{TestId}";

    // --- Test 1: both handlers on a single consume channel receive the same message ---

    [Test]
    public async Task OrderPlaced_TwoHandlersOnChannel_BothInvoked()
    {
        // Simulates publisher publishing OrderPlaced and two independent handlers
        // (inventory-like and notification-like) both receiving it.
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
                        .WithHandler<InventoryOrderPlacedHandler>()
                        .WithHandler<NotificationOrderPlacedHandler>()));
            });
        });

        using (var scope = Services.CreateScope())
        {
            var tm = scope.ServiceProvider.GetRequiredService<RabbitMqTopologyManager>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await tm.WaitForProvisioningAsync(cts.Token);
        }

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new OrderPlaced { OrderId = "fan-out-1" });
        });

        await WaitForConditionAsync(
            () => inventoryCounter.Count >= 1 && notificationCounter.Count >= 1,
            TimeSpan.FromSeconds(20),
            "Both handlers should receive OrderPlaced");

        inventoryCounter.Count.Should().Be(1);
        notificationCounter.Count.Should().Be(1);
    }

    // --- Test 2: inbox deduplication — same message ID delivered twice → handler invoked once ---

    [Test]
    public async Task InboxDedup_DuplicateMessageId_HandlerInvokedOnce()
    {
        // Simulates consumer command inbox dedup: if the same CloudEvents message ID is
        // delivered twice (e.g., via a replay), the inbox constraint
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
                        m.WithHandler<ProcessCommandHandler>(EcommerceHandlerKeys.InventoryProcessOrder))
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
        // CommandPublish only validates the exchange; a CommandConsume on the same channel
        // must exist so RabbitMqTopologyManager declares the direct exchange first.
        var noopCmdQueue = $"noop-cmd-{TestId}";
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

                bus.AddCommandConsumeChannel(CmdChannel, c => c
                    .WithRabbitMq(r => r
                        .WithDirectExchange()
                        .WithQueueName(noopCmdQueue)
                        .WithTransientQueue()
                        .WithQueueType(QueueType.Classic))
                    .Consumes<ProcessOrderCommand>(m =>
                        m.WithHandler<NoOpProcessOrderCommandHandler>()));
            });

            services.AddDbContext<TestDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(PostgresConnectionString);
                opts.RegisterOutbox<TestDbContext>(sp);
            });
        });
        await InitializeDatabase();

        using (var scope = Services.CreateScope())
        {
            var tm = scope.ServiceProvider.GetRequiredService<RabbitMqTopologyManager>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await tm.WaitForProvisioningAsync(cts.Token);
        }

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
        // Simulates consumer inventory failure mode:
        // a handler that always throws exhausts retries and the inbox handler status is poisoned.
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(i => i
                        .WithoutBackgroundProcessing()
                        .WithMaxRetries(3)
                        .WithMaxRetryDelay(TimeSpan.FromMilliseconds(100))));

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
                        m.WithHandler<AlwaysFailingCommandHandler>(EcommerceHandlerKeys.InventoryProcessOrder))
                    .UseInbox<TestDbContext>());
            });

            services.AddDbContext<TestDbContext>((_, opts) => opts.UseNpgsql(PostgresConnectionString));
        });
        await InitializeDatabase();

        using (var scope = Services.CreateScope())
        {
            var tm = scope.ServiceProvider.GetRequiredService<RabbitMqTopologyManager>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await tm.WaitForProvisioningAsync(cts.Token);
        }

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

        // Drive the inbox processor through enough cycles to exhaust retries (respect NextAttemptAt backoff)
        for (var i = 0; i < 40; i++)
        {
            await InScopeAsync(async ctx =>
            {
                var processor = ctx.ServiceProvider.GetRequiredService<InboxMessageProcessor<TestDbContext>>();
                await processor.ProcessBatchAsync(includeStuckMessageDetection: true, CancellationToken.None);
            });
            await Task.Delay(150);
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

    [Test]
    public async Task Inbox_HandlerSucceedsAfterTwoFailures_ThenCompletesWithoutPoison()
    {
        var failState = new SucceedAfterProcessState { FailuresRemaining = 2 };
        var counter = new InventoryCounter();

        await StartTestAsync(services =>
        {
            services.AddSingleton(failState);
            services.AddSingleton(counter);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(i => i.WithoutBackgroundProcessing()));

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
                        m.WithHandler<SucceedAfterProcessHandler>("succeed-after-handler"))
                    .UseInbox<TestDbContext>());
            });

            services.AddDbContext<TestDbContext>((_, opts) => opts.UseNpgsql(PostgresConnectionString));
        });
        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new ProcessOrderCommand { OrderId = "order-sa-1" },
                new MessageProperties { Id = "succeed-after-msg-1" });
        });

        for (var i = 0; i < 25; i++)
        {
            await InScopeAsync(async ctx =>
            {
                var processor = ctx.ServiceProvider.GetRequiredService<InboxMessageProcessor<TestDbContext>>();
                await processor.ProcessBatchAsync(includeStuckMessageDetection: true, CancellationToken.None);
            });
            if (counter.Count >= 1)
                break;
            await Task.Delay(200);
        }

        counter.Count.Should().Be(1);
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var poisoned = await db.Set<InboxHandlerStatusEntity>().AnyAsync(s => s.IsPoisoned);
            poisoned.Should().BeFalse();
        });
    }

    [Test]
    public async Task Outbox_FailingTransportSender_EventuallyProcessesAfterRetries()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var failingSender = new FailingMessageSender("rabbitmq", failuresBeforeSuccess: 2);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(EvtChannel, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseOutbox(o => o.WithoutBackgroundProcessing()));
            });

            services.AddSingleton<OutboxTelemetry>();
            services.AddSingleton<OutboxTriggerInterceptor<TestDbContext>>();
            services.AddTransient<OutboxMessageProcessor<TestDbContext>>();
            services.AddSingleton<OutboxProcessor<TestDbContext>>();
            services.AddSingleton(new OutboxOptionsHolder<TestDbContext>(new OutboxOptions()));
            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(failingSender);
        });
        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.OutboxMessages.Add(new TestEvent { Data = "retry-body" });
            await db.SaveChangesAsync();
        });

        for (var attempt = 0; attempt < 8; attempt++)
        {
            await InScopeAsync(async ctx => await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider));
            var processed = await InScopeAsync(async ctx =>
            {
                var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                var e = await db.Set<OutboxMessageEntity>().FirstAsync();
                return e.ProcessedAt != null;
            });
            if (processed)
                break;
            fakeTime.Advance(TimeSpan.FromMinutes(2));
        }

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var e = await db.Set<OutboxMessageEntity>().FirstAsync();
            e.ProcessedAt.Should().NotBeNull("outbox row should be processed after simulated transport recovers");
            e.ErrorCount.Should().BeGreaterThan(0);
        });
    }

    [Test]
    public async Task EfCoreTransport_PublishDirect_DeliversToInboxHandler()
    {
        var internalChannel = $"orders-internal-{TestId}";
        var counter = new InventoryCounter();

        await StartTestAsync(services =>
        {
            services.AddSingleton(counter);
            services.AddRatatoskr(bus =>
            {
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(i => i.WithoutBackgroundProcessing()));

                bus.AddEventPublishChannel(internalChannel, c => c
                    .WithEfCore()
                    .Produces<OrderPlaced>());

                bus.AddEventConsumeChannel(internalChannel, c => c
                    .Consumes<OrderPlaced>(m => m.WithHandler<EfCoreDemoOrderPlacedHandler>("ef-inbox-demo"))
                    .UseInbox<TestDbContext>());
            });

            services.AddDbContext<TestDbContext>((_, opts) => opts.UseNpgsql(PostgresConnectionString));
        });
        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new OrderPlaced { OrderId = "efcore-order-1" },
                new MessageProperties { Id = "efcore-msg-1" });
        });

        for (var i = 0; i < 20; i++)
        {
            await InScopeAsync(async ctx =>
            {
                var processor = ctx.ServiceProvider.GetRequiredService<InboxMessageProcessor<TestDbContext>>();
                await processor.ProcessBatchAsync(includeStuckMessageDetection: true, CancellationToken.None);
            });
            if (counter.Count >= 1)
                break;
            await Task.Delay(150);
        }

        counter.Count.Should().Be(1);
    }

    [Test]
    public async Task Outbox_OversizedStagedMessage_SaveChangesRollsBackEntities()
    {
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(EvtChannel, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseOutbox(o => o.WithoutBackgroundProcessing().WithMaxMessageSize(2048)));
            });

            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
        });
        await InitializeDatabase();

        var entityId = Guid.NewGuid();
        Func<Task> act = async () => await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.TestEntities.Add(new TestEntity { Id = entityId, Name = "rolled-back" });
            db.OutboxMessages.Add(new TestEvent { Data = new string('z', 10_000) });
            await db.SaveChangesAsync();
        });

        await act.Should().ThrowAsync<Exception>();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            (await db.TestEntities.AnyAsync(e => e.Id == entityId)).Should().BeFalse();
            (await db.Set<OutboxMessageEntity>().CountAsync()).Should().Be(0);
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

    private sealed class SucceedAfterProcessState
    {
        public int FailuresRemaining;
    }

    private sealed class SucceedAfterProcessHandler(SucceedAfterProcessState state, InventoryCounter counter)
        : IMessageHandler<ProcessOrderCommand>
    {
        public Task HandleAsync(ProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
        {
            if (state.FailuresRemaining > 0)
            {
                state.FailuresRemaining--;
                throw new InvalidOperationException("simulated fail");
            }

            counter.Increment();
            return Task.CompletedTask;
        }
    }

    private sealed class EfCoreDemoOrderPlacedHandler(InventoryCounter counter) : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
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

    /// <summary>Only used so topology provisioning declares the command exchange before CommandPublish validates it.</summary>
    private sealed class NoOpProcessOrderCommandHandler : IMessageHandler<ProcessOrderCommand>
    {
        public Task HandleAsync(ProcessOrderCommand message, MessageProperties props, CancellationToken ct)
            => Task.CompletedTask;
    }

    [Test]
    public void PlaygroundMessageIds_TryParseOrderId_AllStableIdsRoundTrip()
    {
        var g = Guid.Parse("a1b2c3d4-e5f6-4789-a012-3456789abcde");
        foreach (var id in new[]
                 {
                     PlaygroundMessageIds.OrderPlaced(g),
                     PlaygroundMessageIds.ProcessOrderCommand(g),
                     PlaygroundMessageIds.OrderFulfilled(g),
                     PlaygroundMessageIds.OrderFailed(g),
                     PlaygroundMessageIds.ReserveStockInternal(g),
                 })
        {
            PlaygroundMessageIds.TryParseOrderId(id, out var parsed).Should().BeTrue();
            parsed.Should().Be(g);
        }

        PlaygroundMessageIds.TryParseOrderId("not-an-order-id", out _).Should().BeFalse();
    }
}
