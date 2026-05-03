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

namespace PlaygroundHost.Scenarios.Inbox.InboxPoison;

public sealed class InboxPoisonScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "inbox-poison";

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
            .Produces<InboxPoisonReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<InboxPoisonReserveStockInternal>(m => m.WithHandler<InboxPoisonReserveStockInternalHandler>($"{ScenarioSlug}.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<InboxPoisonOrderPlaced>()
            .Produces<InboxPoisonOrderFulfilled>()
            .Produces<InboxPoisonOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<InboxPoisonProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<InboxPoisonOrderFulfilled>(m => m.WithHandler<InboxPoisonOrderFulfilledHandler>($"{ScenarioSlug}.fulfilled"))
            .Consumes<InboxPoisonOrderFailed>(m => m.WithHandler<InboxPoisonOrderFailedHandler>($"{ScenarioSlug}.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{ScenarioSlug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<InboxPoisonProcessOrderCommand>(m => m.WithHandler<InboxPoisonProcessOrderHandler>($"{ScenarioSlug}.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<InboxPoisonOrderPlaced>(m => m
                .WithHandler<InboxPoisonOrderPlacedNotifyHandler>($"{ScenarioSlug}.notify")
                .WithHandler<InboxPoisonOrderPlacedAnalyticsHandler>($"{ScenarioSlug}.analytics"))
            .Consumes<InboxPoisonOrderFulfilled>(m => m.WithHandler<InboxPoisonOrderFulfilledNotifyHandler>($"{ScenarioSlug}.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public string Slug => ScenarioSlug;

    public string Title => "Inventory inbox poison";

    public string Description =>
        "Inventory command handler throws until a poisoned inbox row appears for this run.";

    public string Topic => "Inbox";

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
        db.OutboxMessages.Add(new InboxPoisonOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new InboxPoisonProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new InboxPoisonReserveStockInternal(orderIdStr, runId), mpRes);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var pub = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        int before;
        await using (var conScope = context.ScopeFactory.CreateAsyncScope())
        {
            var conDb = conScope.ServiceProvider.GetRequiredService<ConsumerDbContext>();
            before = await PlaygroundSqlMetrics.CountPoisonedInboxForScenarioRunAsync(conDb, runId, cancellationToken);
        }

        await StageOrderAsync(pub, time, runId, cancellationToken);
        context.StepsCompleted.Add("inventory_throw_mode");

        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(90);
        while (time.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope2 = context.ScopeFactory.CreateAsyncScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ConsumerDbContext>();
            var after = await PlaygroundSqlMetrics.CountPoisonedInboxForScenarioRunAsync(db2, runId, cancellationToken);
            if (after > before)
                return new ScenarioVerdict(true, details: new { before, after });

            await Task.Delay(800, cancellationToken);
        }

        await using var conFinal = context.ScopeFactory.CreateAsyncScope();
        var conDbFinal = conFinal.ServiceProvider.GetRequiredService<ConsumerDbContext>();
        var final = await PlaygroundSqlMetrics.CountPoisonedInboxForScenarioRunAsync(conDbFinal, runId, cancellationToken);
        return new ScenarioVerdict(
            false,
            $"Poisoned consumer inbox count did not increase (before={before}, after={final}).");
    }
}
