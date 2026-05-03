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

namespace PlaygroundHost.Scenarios.Other.ReplayDedups;

public sealed class ReplayDedupsScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "replay-dedups";

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
            .Produces<ReplayDedupsReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<ReplayDedupsReserveStockInternal>(m => m.WithHandler<ReplayDedupsReserveStockInternalHandler>($"{ScenarioSlug}.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<ReplayDedupsOrderPlaced>()
            .Produces<ReplayDedupsOrderFulfilled>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<ReplayDedupsProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<ReplayDedupsOrderFulfilled>(m => m.WithHandler<ReplayDedupsOrderFulfilledHandler>($"{ScenarioSlug}.fulfilled"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{ScenarioSlug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<ReplayDedupsProcessOrderCommand>(m => m.WithHandler<ReplayDedupsProcessOrderHandler>($"{ScenarioSlug}.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<ReplayDedupsOrderPlaced>(m => m
                .WithHandler<ReplayDedupsOrderPlacedNotifyHandler>($"{ScenarioSlug}.notify")
                .WithHandler<ReplayDedupsOrderPlacedAnalyticsHandler>($"{ScenarioSlug}.analytics"))
            .UseInbox<PublisherDbContext>());
    }

    public string Slug => ScenarioSlug;

    public string Title => "Replay (dedup vs double delivery)";

    public string Description =>
        "After Fulfilled, replay publishes the same CloudEvents ids; notifications may see duplicate OrderPlaced activity while inventory dedups the command inbox.";

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
        db.OutboxMessages.Add(new ReplayDedupsOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new ReplayDedupsProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new ReplayDedupsReserveStockInternal(orderIdStr, runId), mpRes);
        await db.SaveChangesAsync(cancellationToken);
        return order.Id;
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var bus = sp.GetRequiredService<IRatatoskr>();
        var recorder = sp.GetRequiredService<PlaygroundActivityRecorder>();
        var runId = context.ScenarioRunId;
        var orderId = await StageOrderAsync(db, time, runId, cancellationToken);
        var v = await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            orderId,
            OrderStatus.Fulfilled,
            TimeSpan.FromSeconds(90),
            time,
            cancellationToken);
        if (!v.Passed)
            return v;

        var before = recorder.GetEntriesForOrder(orderId).Count;
        var orderIdStr = orderId.ToString();
        var p1 = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(orderId) };
        var p2 = new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(orderId) };
        var p3 = new MessageProperties { Id = PlaygroundMessageIds.ReserveStockInternal(orderId) };
        PlaygroundCorrelation.AttachToMessageProperties(p1, runId);
        PlaygroundCorrelation.AttachToMessageProperties(p2, runId);
        PlaygroundCorrelation.AttachToMessageProperties(p3, runId);
        await bus.PublishDirectAsync(new ReplayDedupsOrderPlaced(orderIdStr, runId), p1, cancellationToken);
        await bus.PublishDirectAsync(new ReplayDedupsProcessOrderCommand(orderIdStr, runId), p2, cancellationToken);
        await bus.PublishDirectAsync(new ReplayDedupsReserveStockInternal(orderIdStr, runId), p3, cancellationToken);
        context.StepsCompleted.Add("replay_direct_publish");

        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        var after = recorder.GetEntriesForOrder(orderId).Count;
        var nPlaced = recorder.GetEntriesForOrder(orderId).Count(e =>
            e.Stage == nameof(MessageStage.Dispatched) &&
            e.IsSuccess == true &&
            (e.MessageType ?? "").Contains("order-placed", StringComparison.OrdinalIgnoreCase));
        var pass = after > before && nPlaced >= 2;
        return pass
            ? new ScenarioVerdict(true, details: new { rowsBefore = before, rowsAfter = after, notifDispatched = nPlaced })
            : new ScenarioVerdict(
                false,
                $"Activity after replay did not match expectations (before={before}, after={after}, notifOrderPlacedDispatched={nPlaced}).");
    }
}
