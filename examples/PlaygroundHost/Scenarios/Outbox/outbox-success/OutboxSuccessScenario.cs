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

namespace PlaygroundHost.Scenarios.Outbox.OutboxSuccess;

public sealed class OutboxSuccessScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "outbox-success";

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
            .Produces<ReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<ReserveStockInternal>(m => m.WithHandler<ReserveStockInternalHandler>($"{ScenarioSlug}.reserve"))
            .UseInbox<PublisherDbContext>());

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

    public string Title => "Outbox happy path";

    public string Description =>
        "Creates an order on the publisher database with outbox staging; consumer fulfills; publisher reaches Fulfilled.";

    public string Topic => "Outbox";

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
        db.OutboxMessages.Add(new OrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new ProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new ReserveStockInternal(orderIdStr, runId), mpRes);
        await db.SaveChangesAsync(cancellationToken);
        return order.Id;
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        var orderId = await StageOrderAsync(db, time, runId, cancellationToken);
        context.StepsCompleted.Add("order_persisted_outbox_staged");
        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            orderId,
            OrderStatus.Fulfilled,
            TimeSpan.FromSeconds(60),
            time,
            cancellationToken);
    }

    [RatatoskrMessage("outbox-success.order-placed")]
    public sealed record OrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    [RatatoskrMessage("outbox-success.process-order-command")]
    public sealed record ProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    [RatatoskrMessage("outbox-success.reserve-stock-internal")]
    public sealed record ReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    [RatatoskrMessage("outbox-success.order-fulfilled")]
    public sealed record OrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    public sealed class ReserveStockInternalHandler(ILogger<ReserveStockInternalHandler> logger) : IMessageHandler<ReserveStockInternal>
    {
        public Task HandleAsync(ReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
        {
            logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
            return Task.CompletedTask;
        }
    }

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
