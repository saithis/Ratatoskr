using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.Outbox.OutboxPoison;

public sealed class OutboxPoisonScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "outbox-poison";

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues =>
    [
        new("orders", PlaygroundAmqpNames.OrdersQueue(ScenarioSlug)),
        new("inventory", PlaygroundAmqpNames.InventoryQueue(ScenarioSlug)),
        new("notifications", PlaygroundAmqpNames.NotificationsQueue(ScenarioSlug)),
    ];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var exEvt = PlaygroundAmqpNames.EventsExchange(ScenarioSlug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(ScenarioSlug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(ScenarioSlug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(ScenarioSlug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(ScenarioSlug);
        var internalCh = $"pg.{ScenarioSlug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<OutboxPoisonReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<OutboxPoisonReserveStockInternal>(m => m.WithHandler<OutboxPoisonReserveStockInternalHandler>($"{ScenarioSlug}.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<OutboxPoisonOrderPlaced>()
            .Produces<OutboxPoisonOrderFulfilled>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<OutboxPoisonProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<OutboxPoisonOrderFulfilled>(m => m.WithHandler<OutboxPoisonOrderFulfilledHandler>($"{ScenarioSlug}.fulfilled"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{ScenarioSlug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<OutboxPoisonProcessOrderCommand>(m => m.WithHandler<OutboxPoisonProcessOrderHandler>($"{ScenarioSlug}.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<OutboxPoisonOrderPlaced>(m => m
                .WithHandler<OutboxPoisonOrderPlacedNotifyHandler>($"{ScenarioSlug}.notify")
                .WithHandler<OutboxPoisonOrderPlacedAnalyticsHandler>($"{ScenarioSlug}.analytics"))
            .UseInbox<PublisherDbContext>());
    }

    public string Slug => ScenarioSlug;

    public string Title => "Outbox poisoned rows";

    public string Description =>
        "Forces publisher outbox transport sends to fail until messages become poisoned for this run.";

    public string Topic => "Outbox";

    private static async Task StageOrderAsync(
        PublisherDbContext db,
        TimeProvider time,
        string runId,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            Status = OrderStatus.Placed,
            CreatedAt = now,
            StatusChangedAt = now,
            PublishOrigin = "outbox",
        };
        db.Orders.Add(order);
        var orderIdStr = order.Id.ToString();
        var mpPlaced = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) };
        var mpCmd = new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(order.Id) };
        var mpRes = new MessageProperties { Id = PlaygroundMessageIds.ReserveStockInternal(order.Id) };
        PlaygroundCorrelation.AttachToMessageProperties(mpPlaced, runId);
        PlaygroundCorrelation.AttachToMessageProperties(mpCmd, runId);
        PlaygroundCorrelation.AttachToMessageProperties(mpRes, runId);
        db.OutboxMessages.Add(new OutboxPoisonOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new OutboxPoisonProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new OutboxPoisonReserveStockInternal(orderIdStr, runId), mpRes);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        var registry = sp.GetRequiredService<OutboxSendFailureRegistry>();
        registry.Register(runId, OutboxSendFailureKind.AlwaysFail, 0);
        try
        {
            var before = await PlaygroundSqlMetrics.CountPoisonedOutboxForScenarioRunAsync(db, runId, cancellationToken);
            await StageOrderAsync(db, time, runId, cancellationToken);
            context.StepsCompleted.Add("staged_always_fail_send");

            var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(90);
            while (time.GetUtcNow() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var scope2 = context.ScopeFactory.CreateAsyncScope();
                var db2 = scope2.ServiceProvider.GetRequiredService<PublisherDbContext>();
                var after = await PlaygroundSqlMetrics.CountPoisonedOutboxForScenarioRunAsync(db2, runId, cancellationToken);
                if (after > before)
                    return new ScenarioVerdict(true, details: new { before, after });

                await Task.Delay(800, cancellationToken);
            }

            await using var scope3 = context.ScopeFactory.CreateAsyncScope();
            var db3 = scope3.ServiceProvider.GetRequiredService<PublisherDbContext>();
            var final = await PlaygroundSqlMetrics.CountPoisonedOutboxForScenarioRunAsync(db3, runId, cancellationToken);
            return new ScenarioVerdict(
                false,
                $"Poisoned outbox count did not increase within timeout (before={before}, after={final}).");
        }
        finally
        {
            registry.Unregister(runId);
        }
    }
}
