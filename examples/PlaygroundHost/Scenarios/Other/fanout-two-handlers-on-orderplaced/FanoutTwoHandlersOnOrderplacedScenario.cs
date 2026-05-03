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
        [new("notifications", PlaygroundAmqpNames.NotificationsQueue(ScenarioSlug))];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var exEvt = PlaygroundAmqpNames.EventsExchange(ScenarioSlug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(ScenarioSlug);

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<OrderPlaced>());

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

    public string Title => "Fan-out: two OrderPlaced handlers";

    public string Description =>
        "Both notification handlers run for each successful OrderPlaced delivery; activity log should show at least two successful dispatches.";

    public string Topic => "Other";

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
        PlaygroundCorrelation.AttachToMessageProperties(mpPlaced, runId);
        db.OutboxMessages.Add(new OrderPlaced(orderIdStr, runId), mpPlaced);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var sp = context.Services;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var recorder = sp.GetRequiredService<PlaygroundActivityRecorder>();
        var runId = context.ScenarioRunId;
        await StageOrderAsync(db, time, runId, cancellationToken);
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

    [RatatoskrMessage("fanout-two-handlers-on-orderplaced.order-placed")]
    public sealed record OrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

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
