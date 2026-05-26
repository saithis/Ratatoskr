using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.Inbox;

public sealed class InboxDedupsScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "inbox-dedups";

    private const string MessageType = "inbox-dedups.order-placed";

    private static string InboxQueue { get; } =
        PlaygroundAmqpNames.QueueName(ScenarioSlug, "with-inbox");
    private static string NonInboxQueue { get; } =
        PlaygroundAmqpNames.QueueName(ScenarioSlug, "no-inbox");

    public static IReadOnlyList<PlaygroundRabbitQueue> RabbitQueues =>
        [new("inbox-dedup", InboxQueue), new("direct-double", NonInboxQueue)];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var exchangeName = PlaygroundAmqpNames.ExchangeName(ScenarioSlug, "inbox-dedup");

        bus.AddEventPublishChannel(
            exchangeName,
            c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<OrderPlaced>()
        );

        bus.AddEventConsumeChannel(
            $"{ScenarioSlug}-inbox",
            c =>
                c.WithRabbitMq(r =>
                        r.WithTopicExchange()
                            .WithAmqpExchangeName(exchangeName)
                            .WithQueueName(InboxQueue)
                            .WithQueueType(QueueType.Classic)
                            .WithRetry(3, TimeSpan.FromSeconds(1))
                    )
                    .Consumes<OrderPlaced>(m =>
                        m.WithHandler<OrderPlacedInboxHandler>($"{ScenarioSlug}.inbox")
                    )
                    .UseInbox<PublisherDbContext>()
        );

        bus.AddEventConsumeChannel(
            $"{ScenarioSlug}-direct",
            c =>
                c.WithRabbitMq(r =>
                        r.WithTopicExchange()
                            .WithAmqpExchangeName(exchangeName)
                            .WithQueueName(NonInboxQueue)
                            .WithQueueType(QueueType.Classic)
                            .WithRetry(3, TimeSpan.FromSeconds(1))
                    )
                    .Consumes<OrderPlaced>(m => m.WithHandler<OrderPlacedDirectHandler>())
                    .AllowConsumeWithoutInbox()
        );
    }

    public string Slug => ScenarioSlug;

    public string Title => "Dedup (inbox dedup vs double delivery)";

    public string Description =>
        "Publishes OrderPlaced twice with the same CloudEvents id: the inbox-backed consumer runs once; the direct consumer runs twice.";

    public string Topic => "Inbox";

    public async Task<ScenarioVerdict> ExecuteAsync(
        ScenarioExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var recorder = context.GetRequired<PlaygroundActivityRecorder>();
        var fakeOrderId = Guid.NewGuid();

        var props = this.CreateMessageProperties(
            context,
            PlaygroundMessageIds.OrderPlaced(fakeOrderId)
        );
        var evt = new OrderPlaced(fakeOrderId.ToString(), context.ScenarioRunId);

        await context.Ratatoskr.PublishDirectAsync(evt, props, cancellationToken);
        await context.Ratatoskr.PublishDirectAsync(evt, props, cancellationToken);
        context.StepsCompleted.Add("duplicate_direct_publish");

        await Task.Delay(ScenarioTiming.ReplaySettleDelay, context.TimeProvider, cancellationToken);

        await ScenarioAssertions.WaitUntilAsync(
            context.TimeProvider,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1),
            async ct =>
            {
                ct.ThrowIfCancellationRequested();
                var (i, d) = CountEntries(recorder.GetEntriesForScenarioRun(context.ScenarioRunId));
                return i >= 1 && d >= 2;
            },
            cancellationToken
        );

        var (inbox, direct) = CountEntries(
            recorder.GetEntriesForScenarioRun(context.ScenarioRunId)
        );
        return inbox == 1 && direct == 2
            ? new ScenarioVerdict(
                passed: true,
                details: new { inboxInboxDispatched = inbox, directDispatched = direct }
            )
            : new ScenarioVerdict(
                passed: false,
                $"Expected 1 inbox and 2 direct dispatches (inbox={inbox.ToString(System.Globalization.CultureInfo.InvariantCulture)}, direct={direct.ToString(System.Globalization.CultureInfo.InvariantCulture)})."
            );
    }

    private static (int inbox, int direct) CountEntries(
        IReadOnlyList<PlaygroundActivityEntry> entries
    )
    {
        var inbox = entries.Count(e =>
            string.Equals(e.MessageType, MessageType, StringComparison.Ordinal)
            && string.Equals(
                e.Stage,
                nameof(MessageStage.InboxDispatched),
                StringComparison.Ordinal
            )
            && e.IsSuccess == true
        );
        var direct = entries.Count(e =>
            string.Equals(e.MessageType, MessageType, StringComparison.Ordinal)
            && string.Equals(e.Stage, nameof(MessageStage.Dispatched), StringComparison.Ordinal)
            && string.Equals(
                e.DispatchResult,
                nameof(DispatchResult.Success),
                StringComparison.Ordinal
            )
        );
        return (inbox, direct);
    }

    [RatatoskrMessage(MessageType)]
    public sealed record OrderPlaced(string OrderId, string ScenarioRunId)
        : IPlaygroundCorrelatedOrderMessage;

    public sealed class OrderPlacedInboxHandler : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(
            OrderPlaced message,
            MessageProperties properties,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    public sealed class OrderPlacedDirectHandler : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(
            OrderPlaced message,
            MessageProperties properties,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
