using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.DirectConsume;

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
                .WithRetry(maxRetries: 2, delay: TimeSpan.FromSeconds(2)))
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
        var mainQ = PlaygroundAmqpNames.NotificationsQueue(ScenarioSlug);
        var d0 = await RabbitDlqDepthReader.GetDlqCountAsync(rabbitCs, mainQ, cancellationToken);

        var order = this.AddPlacedOrderToContext(context.PublisherDb, context.TimeProvider, "direct");
        await context.PublisherDb.SaveChangesAsync(cancellationToken);
        
        await context.Ratatoskr.PublishDirectAsync(
            new OrderPlaced(order.Id.ToString(), context.ScenarioRunId), 
            this.CreateMessageProperties(context, PlaygroundMessageIds.OrderPlaced(order.Id)), 
            cancellationToken);
        context.StepsCompleted.Add("direct_publish_always_fail");

        return await ScenarioAssertions.DlqDepthEventuallyExceedsBaselineAsync(
            rabbitCs,
            mainQ,
            d0,
            context.TimeProvider,
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
