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

namespace PlaygroundHost.Scenarios.DirectConsume;

public sealed class DirectConsumeRetryScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "direct-consume-retry";
    private static string ExchangeName { get; } =
        PlaygroundAmqpNames.ExchangeName(ScenarioSlug, "events");
    private static string QueueName { get; } =
        PlaygroundAmqpNames.QueueName(ScenarioSlug, "notifications");

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues =>
        [new("work", QueueName)];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        bus.AddEventPublishChannel(
            ExchangeName,
            c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<RetryDemo>()
        );

        bus.AddEventConsumeChannel(
            $"{ScenarioSlug}-work",
            c =>
                c.WithRabbitMq(r =>
                        r.WithTopicExchange()
                            .WithAmqpExchangeName(ExchangeName)
                            .WithQueueName(QueueName)
                            .WithQueueType(QueueType.Classic)
                            .WithRetry(maxRetries: 2, delay: TimeSpan.FromSeconds(1))
                    )
                    .Consumes<RetryDemo>(m => m.WithHandler<RetryDemoHandler>())
        );
    }

    public string Slug => ScenarioSlug;

    public string Title => "Rabbit consumer retry (no inbox)";

    public string Description =>
        "PublishDirectAsync to a single topic exchange; one fire-and-forget handler fails twice then marks the order Fulfilled (Rabbit retry, no inbox).";

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
            new RetryDemo(order.Id.ToString(), context.ScenarioRunId),
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

    [RatatoskrMessage("direct-consume-retry.retry-demo")]
    public sealed record RetryDemo(string OrderId, string ScenarioRunId)
        : IPlaygroundCorrelatedOrderMessage;

    public sealed class RetryDemoHandler(
        PublisherDbContext context,
        TimeProvider timeProvider,
        ILogger<RetryDemoHandler> logger
    ) : IMessageHandler<RetryDemo>
    {
        private static readonly ConcurrentDictionary<string, int> Attempts = new();

        public async Task HandleAsync(
            RetryDemo message,
            MessageProperties properties,
            CancellationToken cancellationToken
        )
        {
            var key = properties.Id ?? message.OrderId;
            var n = Attempts.AddOrUpdate(key, 1, (_, old) => old + 1);
            if (n <= 2)
                throw new InvalidOperationException(
                    "Simulated Rabbit consumer failure (succeed-after-2)."
                );

            var order = await context.Orders.FirstOrDefaultAsync(
                o => o.Id == Guid.Parse(message.OrderId),
                cancellationToken
            );
            if (order is null)
                return;
            var now = timeProvider.GetUtcNow().UtcDateTime;
            order.Status = OrderStatus.Fulfilled;
            order.StatusChangedAt = now;
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Order {OrderId} marked Fulfilled after Rabbit retries",
                message.OrderId
            );
        }
    }
}
