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

namespace PlaygroundHost.Scenarios.Inbox.BusinessRejection;

public sealed class BusinessRejectionScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "business-rejection";

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues =>
    [
        new("orders", PlaygroundAmqpNames.OrdersQueue(ScenarioSlug)),
        new("inventory", PlaygroundAmqpNames.InventoryQueue(ScenarioSlug)),
    ];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var exEvt = PlaygroundAmqpNames.EventsExchange(ScenarioSlug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(ScenarioSlug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(ScenarioSlug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(ScenarioSlug);

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<OrderFailed>());

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
            .Consumes<OrderFailed>(m => m.WithHandler<OrderFailedHandler>($"{ScenarioSlug}.failed"))
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
    }

    public string Slug => ScenarioSlug;

    public string Title => "Business rejection (OrderFailed)";

    public string Description =>
        "Inventory reject mode stages OrderFailed; publisher marks the order Failed.";

    public string Topic => "Inbox";

    private static async Task<Guid> StageOrderAsync(
        PublisherDbContext db,
        TimeProvider time,
        string runId,
        CancellationToken cancellationToken)
    {
        var order = PlaygroundScenarioStaging.AddPlacedOrderToContext(db, time, "outbox");
        var orderIdStr = order.Id.ToString();
        PlaygroundScenarioStaging.StageCorrelatedOutboxMessage(
            db,
            runId,
            new ProcessOrderCommand(orderIdStr, runId),
            PlaygroundMessageIds.ProcessOrderCommand(order.Id));
        await db.SaveChangesAsync(cancellationToken);
        return order.Id;
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var time = context.GetTimeProvider();
        var db = context.GetPublisherDb();
        var runId = context.ScenarioRunId;
        var orderId = await StageOrderAsync(db, time, runId, cancellationToken);
        context.StepsCompleted.Add("inventory_reject_mode");
        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            orderId,
            OrderStatus.Failed,
            ScenarioTiming.OrderEventuallyLong,
            time,
            cancellationToken);
    }

    [RatatoskrMessage("business-rejection.process-order-command")]
    public sealed record ProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    [RatatoskrMessage("business-rejection.order-failed")]
    public sealed record OrderFailed(string OrderId, string ScenarioRunId, string Reason) : IPlaygroundCorrelatedOrderMessage;

    public sealed class ProcessOrderHandler(ConsumerDbContext db, ILogger<ProcessOrderHandler> _) : IMessageHandler<ProcessOrderCommand>
    {
        public async Task HandleAsync(ProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
        {
            var orderGuid = Guid.Parse(message.OrderId);
            db.OutboxMessages.Add(
                new OrderFailed(
                    message.OrderId,
                    message.ScenarioRunId,
                    "Simulated business rejection."),
                new MessageProperties { Id = PlaygroundMessageIds.OrderFailed(orderGuid) });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public sealed class OrderFailedHandler(PublisherDbContext db, TimeProvider time, ILogger<OrderFailedHandler> logger)
        : IMessageHandler<OrderFailed>
    {
        public async Task HandleAsync(OrderFailed message, MessageProperties properties, CancellationToken cancellationToken)
        {
            var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == Guid.Parse(message.OrderId), cancellationToken);
            if (order is null) return;
            var now = time.GetUtcNow().UtcDateTime;
            order.Status = OrderStatus.Failed;
            order.StatusChangedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Order {OrderId} marked Failed", message.OrderId);
        }
    }
}
