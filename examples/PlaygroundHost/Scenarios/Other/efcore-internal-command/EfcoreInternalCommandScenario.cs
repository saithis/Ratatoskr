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

namespace PlaygroundHost.Scenarios.Other.EfcoreInternalCommand;

public sealed class EfcoreInternalCommandScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "efcore-internal-command";

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
            .Produces<EfcoreInternalCommandReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<EfcoreInternalCommandReserveStockInternal>(m => m.WithHandler<EfcoreInternalCommandReserveStockInternalHandler>($"{ScenarioSlug}.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<EfcoreInternalCommandOrderPlaced>()
            .Produces<EfcoreInternalCommandOrderFulfilled>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<EfcoreInternalCommandProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<EfcoreInternalCommandOrderFulfilled>(m => m.WithHandler<EfcoreInternalCommandOrderFulfilledHandler>($"{ScenarioSlug}.fulfilled"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{ScenarioSlug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<EfcoreInternalCommandProcessOrderCommand>(m => m.WithHandler<EfcoreInternalCommandProcessOrderHandler>($"{ScenarioSlug}.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<EfcoreInternalCommandOrderPlaced>(m => m
                .WithHandler<EfcoreInternalCommandOrderPlacedNotifyHandler>($"{ScenarioSlug}.notify")
                .WithHandler<EfcoreInternalCommandOrderPlacedAnalyticsHandler>($"{ScenarioSlug}.analytics"))
            .UseInbox<PublisherDbContext>());
    }

    public string Slug => ScenarioSlug;

    public string Title => "EF Core internal command";

    public string Description =>
        "ReserveStockInternal is staged in the same SaveChanges as other outbox messages; activity should show EF Core transport handling.";

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
        db.OutboxMessages.Add(new EfcoreInternalCommandOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new EfcoreInternalCommandProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new EfcoreInternalCommandReserveStockInternal(orderIdStr, runId), mpRes);
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
        context.StepsCompleted.Add("staged_with_internal");

        await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
        var entries = recorder.GetEntriesForScenarioRun(runId);
        var hit = entries.Any(e =>
            (e.MessageType ?? "").Contains("reserve-stock-internal", StringComparison.OrdinalIgnoreCase) &&
            ((e.TransportName ?? "").Contains("EfCore", StringComparison.OrdinalIgnoreCase) ||
             e.Stage == nameof(MessageStage.InboxDispatched) ||
             e.Stage == nameof(MessageStage.Dispatched)));
        return hit
            ? new ScenarioVerdict(true)
            : new ScenarioVerdict(false, "No ReserveStockInternal / EF Core transport activity captured for this run yet.");
    }
}
