using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry;

public sealed class DirectConsumeRetryScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "direct-consume-retry";

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues =>
        [new("work", PlaygroundAmqpNames.NotificationsQueue(ScenarioSlug))];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var exEvt = PlaygroundAmqpNames.EventsExchange(ScenarioSlug);
        var qWork = PlaygroundAmqpNames.NotificationsQueue(ScenarioSlug);

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<RetryDemo>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-work", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qWork)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<RetryDemo>(m => m.WithHandler<RetryDemoHandler>()));
    }

    public string Slug => ScenarioSlug;

    public string Title => "Rabbit consumer retry (no inbox)";

    public string Description =>
        "PublishDirectAsync to a single topic exchange; one fire-and-forget handler fails twice then marks the order Fulfilled (Rabbit retry, no inbox).";

    public string Topic => "Direct consume";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var time = context.GetTimeProvider();
        var db = context.GetPublisherDb();
        var bus = context.GetRatatoskr();
        var runId = context.ScenarioRunId;
        var order = PlaygroundScenarioStaging.AddPlacedOrderToContext(db, time, "direct");
        await db.SaveChangesAsync(cancellationToken);
        var orderIdStr = order.Id.ToString();
        var mp = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) };
        PlaygroundCorrelation.AttachToMessageProperties(mp, runId);
        await bus.PublishDirectAsync(new RetryDemo(orderIdStr, runId), mp, cancellationToken);
        context.StepsCompleted.Add("direct_publish_one_message");
        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            order.Id,
            OrderStatus.Fulfilled,
            ScenarioTiming.OrderEventuallyLong,
            time,
            cancellationToken);
    }

    [RatatoskrMessage("direct-consume-retry.retry-demo")]
    public sealed record RetryDemo(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    public sealed class RetryDemoHandler(
        PublisherDbContext db,
        TimeProvider time,
        ILogger<RetryDemoHandler> logger) : IMessageHandler<RetryDemo>
    {
        private static readonly ConcurrentDictionary<string, int> Attempts = new();

        public async Task HandleAsync(RetryDemo message, MessageProperties properties, CancellationToken cancellationToken)
        {
            var key = properties.Id ?? message.OrderId;
            var n = Attempts.AddOrUpdate(key, 1, (_, old) => old + 1);
            if (n <= 2)
                throw new InvalidOperationException("Simulated Rabbit consumer failure (succeed-after-2).");

            var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == Guid.Parse(message.OrderId), cancellationToken);
            if (order is null) return;
            var now = time.GetUtcNow().UtcDateTime;
            order.Status = OrderStatus.Fulfilled;
            order.StatusChangedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Order {OrderId} marked Fulfilled after Rabbit retries", message.OrderId);
        }
    }
}
