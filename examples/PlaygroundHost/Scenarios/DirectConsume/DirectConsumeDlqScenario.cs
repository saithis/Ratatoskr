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
    private static string ExchangeName { get; } = PlaygroundAmqpNames.ExchangeName(ScenarioSlug, "events");
    private static string QueueName { get; } = PlaygroundAmqpNames.QueueName(ScenarioSlug, "notifications");

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues =>
        [new("notifications", QueueName)];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        bus.AddEventPublishChannel(ExchangeName, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<OrderPlaced>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(ExchangeName)
                .WithQueueName(QueueName)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 2, delay: TimeSpan.FromSeconds(1)))
            .Consumes<OrderPlaced>(m => m.WithHandler<AlwaysFailHandler>()));
    }

    public string Slug => ScenarioSlug;

    public string Title => "Notification DLQ (no inbox)";

    public string Description =>
        "PublishDirectAsync; one fire-and-forget handler always fails so this queue's DLQ depth grows after Rabbit retries exhaust.";

    public string Topic => "Direct consume";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var baselineDeadLetterCount = await RabbitDlqDepthReader.GetDlqCountAsync(context.RabbitMqConnectionString, QueueName, cancellationToken);

        var fakeOrderId = Guid.NewGuid();
        
        await context.Ratatoskr.PublishDirectAsync(
            new OrderPlaced(fakeOrderId.ToString(), context.ScenarioRunId), 
            this.CreateMessageProperties(context, PlaygroundMessageIds.OrderPlaced(fakeOrderId)), 
            cancellationToken);
        context.StepsCompleted.Add("direct_publish_always_fail");

        return await ScenarioAssertions.DlqDepthEventuallyExceedsBaselineAsync(
            context.RabbitMqConnectionString,
            QueueName,
            baselineDeadLetterCount,
            context.TimeProvider,
            ScenarioTiming.PollLoopLong,
            ScenarioTiming.DlqPollInterval,
            cancellationToken);
    }

    [RatatoskrMessage("direct-consume-dlq.order-placed")]
    public sealed record OrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    public sealed class AlwaysFailHandler : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced message, MessageProperties properties, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Simulated OrderPlaced failure (DLQ scenario)."));
    }
}
