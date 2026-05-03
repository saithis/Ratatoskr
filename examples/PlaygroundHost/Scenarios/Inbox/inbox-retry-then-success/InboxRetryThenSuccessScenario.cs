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

namespace PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess;

public sealed class InboxRetryThenSuccessScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "inbox-retry-then-success";

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
            .Produces<InboxRetryThenSuccessReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<InboxRetryThenSuccessReserveStockInternal>(m => m.WithHandler<InboxRetryThenSuccessReserveStockInternalHandler>($"{ScenarioSlug}.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<InboxRetryThenSuccessOrderPlaced>()
            .Produces<InboxRetryThenSuccessOrderFulfilled>()
            .Produces<InboxRetryThenSuccessOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<InboxRetryThenSuccessProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<InboxRetryThenSuccessOrderFulfilled>(m => m.WithHandler<InboxRetryThenSuccessOrderFulfilledHandler>($"{ScenarioSlug}.fulfilled"))
            .Consumes<InboxRetryThenSuccessOrderFailed>(m => m.WithHandler<InboxRetryThenSuccessOrderFailedHandler>($"{ScenarioSlug}.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{ScenarioSlug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<InboxRetryThenSuccessProcessOrderCommand>(m => m.WithHandler<InboxRetryThenSuccessProcessOrderHandler>($"{ScenarioSlug}.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<InboxRetryThenSuccessOrderPlaced>(m => m
                .WithHandler<InboxRetryThenSuccessOrderPlacedNotifyHandler>($"{ScenarioSlug}.notify")
                .WithHandler<InboxRetryThenSuccessOrderPlacedAnalyticsHandler>($"{ScenarioSlug}.analytics"))
            .Consumes<InboxRetryThenSuccessOrderFulfilled>(m => m.WithHandler<InboxRetryThenSuccessOrderFulfilledNotifyHandler>($"{ScenarioSlug}.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public string Slug => ScenarioSlug;

    public string Title => "Inventory inbox retry then success";

    public string Description =>
        "ProcessOrderCommand fails twice then succeeds; order reaches Fulfilled.";

    public string Topic => "Inbox";

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
        db.OutboxMessages.Add(new InboxRetryThenSuccessOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new InboxRetryThenSuccessProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new InboxRetryThenSuccessReserveStockInternal(orderIdStr, runId), mpRes);
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
        context.StepsCompleted.Add("inventory_succeed_after_two_failures");
        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            orderId,
            OrderStatus.Fulfilled,
            TimeSpan.FromSeconds(90),
            time,
            cancellationToken);
    }
}
