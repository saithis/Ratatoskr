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

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<OrderPlaced>()
            .Produces<OrderFulfilled>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<ProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<OrderFulfilled>(m => m.WithHandler<OrderFulfilledHandler>($"{ScenarioSlug}.fulfilled"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{ScenarioSlug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<ProcessOrderCommand>(m => m.WithHandler<ProcessOrderHandler>($"{ScenarioSlug}.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<OrderPlaced>(m => m
                .WithHandler<OrderPlacedNotifyHandler>($"{ScenarioSlug}.notify")
                .WithHandler<OrderPlacedAnalyticsHandler>($"{ScenarioSlug}.analytics"))
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
        PlaygroundCorrelation.AttachToMessageProperties(mpPlaced, runId);
        PlaygroundCorrelation.AttachToMessageProperties(mpCmd, runId);
        db.OutboxMessages.Add(new OrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new ProcessOrderCommand(orderIdStr, runId), mpCmd);
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
        PlaygroundCorrelation.AttachToMessageProperties(p1, runId);
        PlaygroundCorrelation.AttachToMessageProperties(p2, runId);
        await bus.PublishDirectAsync(new OrderPlaced(orderIdStr, runId), p1, cancellationToken);
        await bus.PublishDirectAsync(new ProcessOrderCommand(orderIdStr, runId), p2, cancellationToken);
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

    [RatatoskrMessage("replay-dedups.order-placed")]
    public sealed record OrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    [RatatoskrMessage("replay-dedups.process-order-command")]
    public sealed record ProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    [RatatoskrMessage("replay-dedups.order-fulfilled")]
    public sealed record OrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    public sealed class ProcessOrderHandler(ConsumerDbContext db, ILogger<ProcessOrderHandler> _) : IMessageHandler<ProcessOrderCommand>
    {
        public async Task HandleAsync(ProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
        {
            var orderGuid = Guid.Parse(message.OrderId);
            db.OutboxMessages.Add(
                new OrderFulfilled(message.OrderId, message.ScenarioRunId),
                new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public sealed class OrderFulfilledHandler(PublisherDbContext db, TimeProvider time, ILogger<OrderFulfilledHandler> logger)
        : IMessageHandler<OrderFulfilled>
    {
        public async Task HandleAsync(OrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
        {
            var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == Guid.Parse(message.OrderId), cancellationToken);
            if (order is null) return;
            var now = time.GetUtcNow().UtcDateTime;
            order.Status = OrderStatus.Fulfilled;
            order.StatusChangedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Order {OrderId} marked Fulfilled", message.OrderId);
        }
    }

    public sealed class OrderPlacedNotifyHandler(ILogger<OrderPlacedNotifyHandler> _) : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced message, MessageProperties properties, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    public sealed class OrderPlacedAnalyticsHandler(ILogger<OrderPlacedAnalyticsHandler> _) : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced message, MessageProperties properties, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
