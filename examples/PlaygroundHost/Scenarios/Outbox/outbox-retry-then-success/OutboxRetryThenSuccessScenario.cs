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

namespace PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess;

public sealed class OutboxRetryThenSuccessScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "outbox-retry-then-success";

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
            .Produces<OutboxRetryThenSuccessReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<OutboxRetryThenSuccessReserveStockInternal>(m => m.WithHandler<OutboxRetryThenSuccessReserveStockInternalHandler>($"{ScenarioSlug}.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<OutboxRetryThenSuccessOrderPlaced>()
            .Produces<OutboxRetryThenSuccessOrderFulfilled>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<OutboxRetryThenSuccessProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<OutboxRetryThenSuccessOrderFulfilled>(m => m.WithHandler<OutboxRetryThenSuccessOrderFulfilledHandler>($"{ScenarioSlug}.fulfilled"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{ScenarioSlug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<OutboxRetryThenSuccessProcessOrderCommand>(m => m.WithHandler<OutboxRetryThenSuccessProcessOrderHandler>($"{ScenarioSlug}.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<OutboxRetryThenSuccessOrderPlaced>(m => m
                .WithHandler<OutboxRetryThenSuccessOrderPlacedNotifyHandler>($"{ScenarioSlug}.notify")
                .WithHandler<OutboxRetryThenSuccessOrderPlacedAnalyticsHandler>($"{ScenarioSlug}.analytics"))
            .UseInbox<PublisherDbContext>());
    }

    public string Slug => ScenarioSlug;

    public string Title => "Outbox relay retries then succeeds";

    public string Description =>
        "Simulates transport failures on the publisher outbox send path for this run, then succeeds; order reaches Fulfilled.";

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
        db.OutboxMessages.Add(new OutboxRetryThenSuccessOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new OutboxRetryThenSuccessProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new OutboxRetryThenSuccessReserveStockInternal(orderIdStr, runId), mpRes);
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
        var registry = sp.GetRequiredService<OutboxSendFailureRegistry>();
        registry.Register(runId, OutboxSendFailureKind.SucceedAfterNFailures, 2);
        try
        {
            var orderId = await StageOrderAsync(db, time, runId, cancellationToken);
            context.StepsCompleted.Add("staged_with_send_failures");
            return await ScenarioAssertions.OrderEventuallyAsync(
                context.ScopeFactory,
                orderId,
                OrderStatus.Fulfilled,
                TimeSpan.FromSeconds(90),
                time,
                cancellationToken);
        }
        finally
        {
            registry.Unregister(runId);
        }
    }
}
