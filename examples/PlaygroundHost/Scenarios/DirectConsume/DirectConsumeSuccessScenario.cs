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

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues =>
        [new("work", PlaygroundAmqpNames.NotificationsQueue(ScenarioSlug))];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var exEvt = PlaygroundAmqpNames.EventsExchange(ScenarioSlug);
        var qWork = PlaygroundAmqpNames.NotificationsQueue(ScenarioSlug);

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<DirectWork>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-work", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qWork)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<DirectWork>(m => m.WithHandler<DirectWorkHandler>()));
    }

    public string Slug => ScenarioSlug;

    public string Title => "Direct publish happy path";

    public string Description =>
        "PublishDirectAsync to one topic exchange and one consumer (no inbox); handler marks the order Fulfilled.";

    public string Topic => "Direct consume";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var order = this.AddPlacedOrderToContext(context.PublisherDb, context.TimeProvider, "direct");
        await context.PublisherDb.SaveChangesAsync(cancellationToken);
        
        await context.Ratatoskr.PublishDirectAsync(
            new DirectWork(order.Id.ToString(), context.ScenarioRunId), 
            this.CreateMessageProperties(context, PlaygroundMessageIds.OrderPlaced(order.Id)), 
            cancellationToken);
        context.StepsCompleted.Add("direct_publish_one_message");
        
        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            order.Id,
            OrderStatus.Fulfilled,
            ScenarioTiming.OrderEventuallyLong,
            context.TimeProvider,
            cancellationToken);
    }

    [RatatoskrMessage("direct-consume-success.direct-work")]
    public sealed record DirectWork(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    public sealed class DirectWorkHandler(PublisherDbContext context, TimeProvider timeProvider, ILogger<DirectWorkHandler> _)
        : IMessageHandler<DirectWork>
    {
        public Task HandleAsync(DirectWork message, MessageProperties _, CancellationToken cancellationToken) =>
            IScenarioExtensions.UpdateOrderStatusAsync(null!, context, timeProvider, message.OrderId, OrderStatus.Fulfilled, cancellationToken);
    }
}
