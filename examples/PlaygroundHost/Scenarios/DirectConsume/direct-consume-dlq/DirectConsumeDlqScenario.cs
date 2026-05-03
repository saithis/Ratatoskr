using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq;

public sealed class DirectConsumeDlqScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "direct-consume-dlq";

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
            .Consumes<OrderPlaced>(m => m.WithHandler<AlwaysFailHandler>()));
    }

    public string Slug => ScenarioSlug;

    public string Title => "Notification DLQ (no inbox)";

    public string Description =>
        "PublishDirectAsync; one fire-and-forget handler always fails so this queue's DLQ depth grows after Rabbit retries exhaust.";

    public string Topic => "Direct consume";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var cfg = context.GetRequired<IConfiguration>();
        var rabbitCs = cfg.GetConnectionString("rabbitmq")
            ?? throw new InvalidOperationException("rabbitmq connection string missing.");
        var time = context.GetTimeProvider();
        var db = context.GetPublisherDb();
        var bus = context.GetRatatoskr();
        var runId = context.ScenarioRunId;
        var mainQ = PlaygroundAmqpNames.NotificationsQueue(ScenarioSlug);
        var d0 = await RabbitDlqDepthReader.GetDlqCountAsync(rabbitCs, mainQ, cancellationToken);

        var order = PlaygroundScenarioStaging.AddPlacedOrderToContext(db, time, "direct");
        await db.SaveChangesAsync(cancellationToken);
        var orderIdStr = order.Id.ToString();
        var mpPlaced = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) };
        PlaygroundCorrelation.AttachToMessageProperties(mpPlaced, runId);
        await bus.PublishDirectAsync(new OrderPlaced(orderIdStr, runId), mpPlaced, cancellationToken);
        context.StepsCompleted.Add("direct_publish_always_fail");

        return await ScenarioAssertions.DlqDepthEventuallyExceedsBaselineAsync(
            rabbitCs,
            mainQ,
            d0,
            time,
            ScenarioTiming.PollLoopLong,
            ScenarioTiming.DlqPollInterval,
            cancellationToken);
    }

    [RatatoskrMessage("direct-consume-dlq.order-placed")]
    public sealed record OrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    public sealed class AlwaysFailHandler(ILogger<AlwaysFailHandler> _) : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced message, MessageProperties properties, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Simulated OrderPlaced failure (DLQ scenario)."));
    }
}
