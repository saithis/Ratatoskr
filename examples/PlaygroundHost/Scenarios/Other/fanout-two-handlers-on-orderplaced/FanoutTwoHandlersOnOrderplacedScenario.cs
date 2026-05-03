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
        var order = PlaygroundScenarioStaging.AddPlacedOrderToContext(db, time, "outbox");
        var orderIdStr = order.Id.ToString();
        PlaygroundScenarioStaging.StageCorrelatedOutboxMessage(
            db,
            runId,
            new OrderPlaced(orderIdStr, runId),
            PlaygroundMessageIds.OrderPlaced(order.Id));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        static int CountSuccessfulOrderPlacedDispatches(IReadOnlyList<PlaygroundActivityEntry> entries) =>
            entries.Count(e =>
                e.Stage == nameof(MessageStage.Dispatched) &&
                e.IsSuccess == true &&
                (e.MessageType ?? "").Contains("order-placed", StringComparison.OrdinalIgnoreCase));

        var time = context.GetTimeProvider();
        var db = context.GetPublisherDb();
        var recorder = context.GetRequired<PlaygroundActivityRecorder>();
        var runId = context.ScenarioRunId;
        await StageOrderAsync(db, time, runId, cancellationToken);
        context.StepsCompleted.Add("staged_for_fanout");

        var ok = await ScenarioAssertions.WaitUntilAsync(
            time,
            ScenarioTiming.PollLoopLong,
            ScenarioTiming.OrderPollInterval,
            async ct =>
            {
                ct.ThrowIfCancellationRequested();
                var entries = recorder.GetEntriesForScenarioRun(runId);
                return CountSuccessfulOrderPlacedDispatches(entries) >= 2;
            },
            cancellationToken);

        if (ok)
        {
            var entries = recorder.GetEntriesForScenarioRun(runId);
            var okCount = CountSuccessfulOrderPlacedDispatches(entries);
            return new ScenarioVerdict(true, details: new { matchingRows = okCount });
        }

        var final = recorder.GetEntriesForScenarioRun(runId);
        var n = CountSuccessfulOrderPlacedDispatches(final);
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
