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
        new("inbox-dedup", PlaygroundAmqpNames.ReplayDedupInboxQueue(ScenarioSlug)),
        new("direct-double", PlaygroundAmqpNames.ReplayDedupDirectQueue(ScenarioSlug)),
    ];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var exEvt = PlaygroundAmqpNames.EventsExchange(ScenarioSlug);
        var qInbox = PlaygroundAmqpNames.ReplayDedupInboxQueue(ScenarioSlug);
        var qDirect = PlaygroundAmqpNames.ReplayDedupDirectQueue(ScenarioSlug);

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<OrderPlaced>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qInbox)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(1)))
            .Consumes<OrderPlaced>(m => m.WithHandler<OrderPlacedInboxHandler>($"{ScenarioSlug}.inbox"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-direct", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qDirect)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(1)))
            .Consumes<OrderPlaced>(m => m.WithHandler<OrderPlacedDirectHandler>())
            .AllowConsumeWithoutInbox());
    }

    public string Slug => ScenarioSlug;

    public string Title => "Replay (inbox dedup vs double delivery)";

    public string Description =>
        "Publishes OrderPlaced twice with the same CloudEvents id: the inbox-backed consumer runs once; the direct consumer runs twice.";

    public string Topic => "Other";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        static bool MetricsOk(IReadOnlyList<PlaygroundActivityEntry> entries)
        {
            var inbox = 0;
            var direct = 0;
            foreach (var e in entries)
            {
                if (!(e.MessageType ?? "").Contains("order-placed", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (e.Stage == nameof(MessageStage.InboxDispatched) && e.IsSuccess == true)
                    inbox++;
                else if (e.Stage == nameof(MessageStage.Dispatched)
                         && e.DispatchResult == nameof(DispatchResult.Success))
                    direct++;
            }

            return inbox >= 1 && direct >= 2;
        }

        var time = context.GetTimeProvider();
        var db = context.GetPublisherDb();
        var bus = context.GetRatatoskr();
        var recorder = context.GetRequired<PlaygroundActivityRecorder>();
        var runId = context.ScenarioRunId;

        var order = PlaygroundScenarioStaging.AddPlacedOrderToContext(db, time, "replay-dedups");
        await db.SaveChangesAsync(cancellationToken);

        var orderIdStr = order.Id.ToString();
        var props = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) };
        PlaygroundCorrelation.AttachToMessageProperties(props, runId);
        var evt = new OrderPlaced(orderIdStr, runId);

        await bus.PublishDirectAsync(evt, props, cancellationToken);
        await bus.PublishDirectAsync(evt, props, cancellationToken);
        context.StepsCompleted.Add("duplicate_direct_publish");

        await Task.Delay(ScenarioTiming.ReplaySettleDelay, cancellationToken);

        var ok = await ScenarioAssertions.WaitUntilAsync(
            time,
            ScenarioTiming.PollLoopLong,
            ScenarioTiming.OrderPollInterval,
            async ct =>
            {
                ct.ThrowIfCancellationRequested();
                return MetricsOk(recorder.GetEntriesForScenarioRun(runId));
            },
            cancellationToken);

        if (ok)
        {
            var entries = recorder.GetEntriesForScenarioRun(runId);
            return new ScenarioVerdict(
                true,
                details: new
                {
                    inboxInboxDispatched = entries.Count(e =>
                        e.Stage == nameof(MessageStage.InboxDispatched) && e.IsSuccess == true
                                                                 && (e.MessageType ?? "").Contains(
                                                                     "order-placed",
                                                                     StringComparison.OrdinalIgnoreCase)),
                    directDispatched = entries.Count(e =>
                        e.Stage == nameof(MessageStage.Dispatched)
                        && e.DispatchResult == nameof(DispatchResult.Success)
                        && (e.MessageType ?? "").Contains("order-placed", StringComparison.OrdinalIgnoreCase)),
                });
        }

        var final = recorder.GetEntriesForScenarioRun(runId);
        return new ScenarioVerdict(
            false,
            $"Expected 1+ successful inbox OrderPlaced dispatches and 2+ direct dispatches for this run; " +
            $"inbox={final.Count(e => e.Stage == nameof(MessageStage.InboxDispatched) && e.IsSuccess == true && (e.MessageType ?? "").Contains("order-placed", StringComparison.OrdinalIgnoreCase))}, " +
            $"direct={final.Count(e => e.Stage == nameof(MessageStage.Dispatched) && e.DispatchResult == nameof(DispatchResult.Success) && (e.MessageType ?? "").Contains("order-placed", StringComparison.OrdinalIgnoreCase))}.");
    }

    [RatatoskrMessage("replay-dedups.order-placed")]
    public sealed record OrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    public sealed class OrderPlacedInboxHandler(ILogger<OrderPlacedInboxHandler> _) : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced message, MessageProperties properties, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    public sealed class OrderPlacedDirectHandler(ILogger<OrderPlacedDirectHandler> _) : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced message, MessageProperties properties, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
