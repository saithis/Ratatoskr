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

namespace PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced;

public sealed class FanoutTwoHandlersOnOrderplacedScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "fanout-two-handlers-on-orderplaced";

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
            .Produces<FanoutTwoHandlersOnOrderplacedReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<FanoutTwoHandlersOnOrderplacedReserveStockInternal>(m => m.WithHandler<FanoutTwoHandlersOnOrderplacedReserveStockInternalHandler>($"{ScenarioSlug}.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<FanoutTwoHandlersOnOrderplacedOrderPlaced>()
            .Produces<FanoutTwoHandlersOnOrderplacedOrderFulfilled>()
            .Produces<FanoutTwoHandlersOnOrderplacedOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<FanoutTwoHandlersOnOrderplacedProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<FanoutTwoHandlersOnOrderplacedOrderFulfilled>(m => m.WithHandler<FanoutTwoHandlersOnOrderplacedOrderFulfilledHandler>($"{ScenarioSlug}.fulfilled"))
            .Consumes<FanoutTwoHandlersOnOrderplacedOrderFailed>(m => m.WithHandler<FanoutTwoHandlersOnOrderplacedOrderFailedHandler>($"{ScenarioSlug}.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{ScenarioSlug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<FanoutTwoHandlersOnOrderplacedProcessOrderCommand>(m => m.WithHandler<FanoutTwoHandlersOnOrderplacedProcessOrderHandler>($"{ScenarioSlug}.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<FanoutTwoHandlersOnOrderplacedOrderPlaced>(m => m
                .WithHandler<FanoutTwoHandlersOnOrderplacedOrderPlacedNotifyHandler>($"{ScenarioSlug}.notify")
                .WithHandler<FanoutTwoHandlersOnOrderplacedOrderPlacedAnalyticsHandler>($"{ScenarioSlug}.analytics"))
            .Consumes<FanoutTwoHandlersOnOrderplacedOrderFulfilled>(m => m.WithHandler<FanoutTwoHandlersOnOrderplacedOrderFulfilledNotifyHandler>($"{ScenarioSlug}.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public string Slug => ScenarioSlug;

    public string Title => "Fan-out: two OrderPlaced handlers";

    public string Description =>
        "Both notification handlers run for each successful OrderPlaced delivery; activity log should show at least two successful dispatches.";

    public string Topic => "Other";

    private static async Task<Guid> StageOrderAsync(
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
        db.OutboxMessages.Add(new FanoutTwoHandlersOnOrderplacedOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new FanoutTwoHandlersOnOrderplacedProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new FanoutTwoHandlersOnOrderplacedReserveStockInternal(orderIdStr, runId), mpRes);
        await db.SaveChangesAsync(cancellationToken);
        return order.Id;
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var recorder = sp.GetRequiredService<PlaygroundActivityRecorder>();
        var runId = context.ScenarioRunId;
        _ = await StageOrderAsync(db, time, runId, cancellationToken);
        context.StepsCompleted.Add("staged_for_fanout");

        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(90);
        while (time.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = recorder.GetEntriesForScenarioRun(runId);
            var ok = entries.Count(e =>
                e.Stage == nameof(MessageStage.Dispatched) &&
                e.IsSuccess == true &&
                (e.MessageType ?? "").Contains("order-placed", StringComparison.OrdinalIgnoreCase));
            if (ok >= 2)
                return new ScenarioVerdict(true, details: new { matchingRows = ok });
            await Task.Delay(500, cancellationToken);
        }

        var final = recorder.GetEntriesForScenarioRun(runId);
        var n = final.Count(e =>
            e.Stage == nameof(MessageStage.Dispatched) &&
            e.IsSuccess == true &&
            (e.MessageType ?? "").Contains("order-placed", StringComparison.OrdinalIgnoreCase));
        return new ScenarioVerdict(false, $"Expected at least 2 successful OrderPlaced handler rows for this run; saw {n}.");
    }
}
