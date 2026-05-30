using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.DirectConsume;

public sealed class DirectConsumeSuccessScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "direct-consume-success";
    private static string ExchangeName { get; } =
        PlaygroundAmqpNames.ExchangeName(ScenarioSlug, "events");
    private static string QueueName { get; } =
        PlaygroundAmqpNames.QueueName(ScenarioSlug, "notifications");

    public static IReadOnlyList<PlaygroundRabbitQueue> RabbitQueues => [new("work", QueueName)];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        bus.AddEventPublishChannel(
            ExchangeName,
            c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<DirectWork>()
        );

        bus.AddEventConsumeChannel(
            $"{ScenarioSlug}-work",
            c =>
                c.WithRabbitMq(r =>
                        r.WithTopicExchange()
                            .WithAmqpExchangeName(ExchangeName)
                            .WithQueueName(QueueName)
                            .WithQueueType(QueueType.Classic)
                            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5))
                    )
                    .Consumes<DirectWork>(m => m.WithHandler<DirectWorkHandler>())
        );
    }

    public string Slug => ScenarioSlug;

    public string Title => "Direct publish happy path";

    public string Description =>
        "PublishDirectAsync to one topic exchange and one consumer (no inbox); handler marks the order Fulfilled.";

    public string Topic => "Direct consume";

    public async Task<ScenarioVerdict> ExecuteAsync(
        ScenarioExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var order = this.AddPlacedOrderToContext(
            context.PublisherDb,
            context.TimeProvider,
            "direct"
        );
        await context.PublisherDb.SaveChangesAsync(cancellationToken);

        await context.Ratatoskr.PublishDirectAsync(
            new DirectWork(order.Id.ToString(), context.ScenarioRunId),
            this.CreateMessageProperties(context, PlaygroundMessageIds.OrderPlaced(order.Id)),
            cancellationToken
        );
        context.StepsCompleted.Add("direct_publish_one_message");

        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            order.Id,
            OrderStatus.Fulfilled,
            ScenarioTiming.OrderEventuallyLong,
            context.TimeProvider,
            cancellationToken
        );
    }

    [RatatoskrMessage("direct-consume-success.direct-work")]
    public sealed record DirectWork(string OrderId, string ScenarioRunId)
        : IPlaygroundCorrelatedOrderMessage;

    public sealed class DirectWorkHandler(PublisherDbContext context, TimeProvider timeProvider)
        : IMessageHandler<DirectWork>
    {
        public Task HandleAsync(
            DirectWork message,
            MessageProperties properties,
            CancellationToken cancellationToken
        ) =>
            IScenarioExtensions.UpdateOrderStatusAsync(
                context,
                timeProvider,
                message.OrderId,
                OrderStatus.Fulfilled,
                cancellationToken
            );
    }
}
