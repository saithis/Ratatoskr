using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.Other;

public sealed class FanoutTwoHandlersOnOrderplacedScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "fanout-two-handlers-on-orderplaced";
    private static string ExchangeName { get; } =
        PlaygroundAmqpNames.ExchangeName(ScenarioSlug, "events");
    private static string QueueName { get; } =
        PlaygroundAmqpNames.QueueName(ScenarioSlug, "notifications");

    public static IReadOnlyList<PlaygroundRabbitQueue> RabbitQueues =>
        [new("notifications", QueueName)];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        bus.AddEventPublishChannel(
            ExchangeName,
            c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<OrderPlaced>()
        );

        bus.AddEventConsumeChannel(
            $"{ScenarioSlug}-notifications",
            c =>
                c.WithRabbitMq(r =>
                        r.WithTopicExchange()
                            .WithAmqpExchangeName(ExchangeName)
                            .WithQueueName(QueueName)
                            .WithQueueType(QueueType.Classic)
                            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5))
                    )
                    .Consumes<OrderPlaced>(m =>
                        m.WithHandler<OrderPlacedNotifyHandler>($"{ScenarioSlug}.notify")
                            .WithHandler<OrderPlacedAnalyticsHandler>($"{ScenarioSlug}.analytics")
                    )
                    .UseInbox<PublisherDbContext>()
        );
    }

    public string Slug => ScenarioSlug;

    public string Title => "Fan-out: two OrderPlaced handlers";

    public string Description =>
        "Both notification handlers run for each successful OrderPlaced delivery; activity log should show at least two successful dispatches.";

    public string Topic => "Other";

    private async Task StageOrderAsync(
        PublisherDbContext context,
        TimeProvider timeProvider,
        string runId,
        CancellationToken cancellationToken
    )
    {
        var order = this.AddPlacedOrderToContext(context, timeProvider, "outbox");
        this.StageCorrelatedOutboxMessage(
            context,
            runId,
            new OrderPlaced(order.Id.ToString(), runId),
            PlaygroundMessageIds.OrderPlaced(order.Id)
        );
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(
        ScenarioExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        static int CountSuccessfulOrderPlacedDispatches(
            IReadOnlyList<PlaygroundActivityEntry> entries
        ) =>
            entries.Count(e =>
                e.Stage == nameof(MessageStage.InboxDispatched)
                && e.IsSuccess == true
                && (e.MessageType ?? "").Contains(
                    "order-placed",
                    StringComparison.OrdinalIgnoreCase
                )
            );

        var recorder = context.GetRequired<PlaygroundActivityRecorder>();
        var runId = context.ScenarioRunId;
        await StageOrderAsync(context.PublisherDb, context.TimeProvider, runId, cancellationToken);
        context.StepsCompleted.Add("staged_for_fanout");

        var ok = await ScenarioAssertions.WaitUntilAsync(
            context.TimeProvider,
            ScenarioTiming.PollLoopLong,
            ScenarioTiming.OrderPollInterval,
            async ct =>
            {
                ct.ThrowIfCancellationRequested();
                var entries = recorder.GetEntriesForScenarioRun(runId);
                return CountSuccessfulOrderPlacedDispatches(entries) >= 2;
            },
            cancellationToken
        );

        if (ok)
        {
            var entries = recorder.GetEntriesForScenarioRun(runId);
            var okCount = CountSuccessfulOrderPlacedDispatches(entries);
            return new ScenarioVerdict(passed: true, details: new { matchingRows = okCount });
        }

        var final = recorder.GetEntriesForScenarioRun(runId);
        var n = CountSuccessfulOrderPlacedDispatches(final);
        return new ScenarioVerdict(
            passed: false,
            $"Expected at least 2 successful OrderPlaced handler rows for this run; saw {n.ToString(System.Globalization.CultureInfo.InvariantCulture)}."
        );
    }

    [RatatoskrMessage("fanout-two-handlers-on-orderplaced.order-placed")]
    public sealed record OrderPlaced(string OrderId, string ScenarioRunId)
        : IPlaygroundCorrelatedOrderMessage;

    public sealed class OrderPlacedNotifyHandler : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(
            OrderPlaced message,
            MessageProperties properties,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    public sealed class OrderPlacedAnalyticsHandler : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(
            OrderPlaced message,
            MessageProperties properties,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
