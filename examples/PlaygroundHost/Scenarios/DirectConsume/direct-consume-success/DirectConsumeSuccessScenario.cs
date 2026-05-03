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

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess;

public sealed class DirectConsumeSuccessScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "direct-consume-success";

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
            .Produces<DirectConsumeSuccessReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<DirectConsumeSuccessReserveStockInternal>(m => m.WithHandler<DirectConsumeSuccessReserveStockInternalHandler>($"{ScenarioSlug}.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<DirectConsumeSuccessOrderPlaced>()
            .Produces<DirectConsumeSuccessOrderFulfilled>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<DirectConsumeSuccessProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<DirectConsumeSuccessOrderFulfilled>(m => m.WithHandler<DirectConsumeSuccessOrderFulfilledHandler>($"{ScenarioSlug}.fulfilled"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{ScenarioSlug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<DirectConsumeSuccessProcessOrderCommand>(m => m.WithHandler<DirectConsumeSuccessProcessOrderHandler>($"{ScenarioSlug}.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<DirectConsumeSuccessOrderPlaced>(m => m
                .WithHandler<DirectConsumeSuccessOrderPlacedNotifyHandler>($"{ScenarioSlug}.notify")
                .WithHandler<DirectConsumeSuccessOrderPlacedAnalyticsHandler>($"{ScenarioSlug}.analytics"))
            .UseInbox<PublisherDbContext>());
    }

    public string Slug => ScenarioSlug;

    public string Title => "Direct publish happy path";

    public string Description =>
        "Persists the order then publishes Rabbit-bound messages with PublishDirectAsync (no outbox); expects Fulfilled.";

    public string Topic => "Direct consume";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var bus = sp.GetRequiredService<IRatatoskr>();
        var runId = context.ScenarioRunId;
        var now = time.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            Status = OrderStatus.Placed,
            CreatedAt = now,
            StatusChangedAt = now,
            PublishOrigin = "direct",
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        var orderIdStr = order.Id.ToString();
        var p1 = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) };
        var p2 = new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(order.Id) };
        var p3 = new MessageProperties { Id = PlaygroundMessageIds.ReserveStockInternal(order.Id) };
        PlaygroundCorrelation.AttachToMessageProperties(p1, runId);
        PlaygroundCorrelation.AttachToMessageProperties(p2, runId);
        PlaygroundCorrelation.AttachToMessageProperties(p3, runId);
        await bus.PublishDirectAsync(new DirectConsumeSuccessOrderPlaced(orderIdStr, runId), p1, cancellationToken);
        await bus.PublishDirectAsync(new DirectConsumeSuccessProcessOrderCommand(orderIdStr, runId), p2, cancellationToken);
        await bus.PublishDirectAsync(new DirectConsumeSuccessReserveStockInternal(orderIdStr, runId), p3, cancellationToken);
        context.StepsCompleted.Add("direct_publish_three_messages");
        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            order.Id,
            OrderStatus.Fulfilled,
            TimeSpan.FromSeconds(90),
            time,
            cancellationToken);
    }
}
