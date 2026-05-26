using System.Collections.Concurrent;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.Inbox;

public sealed class InboxRetryThenSuccessScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "inbox-retry-then-success";
    private static string EventsExchangeName { get; } =
        PlaygroundAmqpNames.ExchangeName(ScenarioSlug, "events");
    private static string CommandsExchangeName { get; } =
        PlaygroundAmqpNames.ExchangeName(ScenarioSlug, "commands");
    private static string OrdersQueueName { get; } =
        PlaygroundAmqpNames.QueueName(ScenarioSlug, "orders");
    private static string InventoryQueueName { get; } =
        PlaygroundAmqpNames.QueueName(ScenarioSlug, "inventory");

    public static IReadOnlyList<PlaygroundRabbitQueue> RabbitQueues =>
        [new("orders", OrdersQueueName), new("inventory", InventoryQueueName)];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        bus.AddEventPublishChannel(
            EventsExchangeName,
            c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<OrderFulfilled>()
        );

        bus.AddCommandPublishChannel(
            CommandsExchangeName,
            c => c.WithRabbitMq(r => r.WithDirectExchange()).Produces<ProcessOrderCommand>()
        );

        bus.AddEventConsumeChannel(
            $"{ScenarioSlug}-orders-inbox",
            c =>
                c.WithRabbitMq(r =>
                        r.WithTopicExchange()
                            .WithAmqpExchangeName(EventsExchangeName)
                            .WithQueueName(OrdersQueueName)
                            .WithQueueType(QueueType.Classic)
                            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(1))
                    )
                    .Consumes<OrderFulfilled>(m =>
                        m.WithHandler<OrderFulfilledHandler>($"{ScenarioSlug}.fulfilled")
                    )
                    .UseInbox<PublisherDbContext>()
        );

        bus.AddCommandConsumeChannel(
            $"{ScenarioSlug}-inventory",
            c =>
                c.WithRabbitMq(r =>
                        r.WithDirectExchange()
                            .WithAmqpExchangeName(CommandsExchangeName)
                            .WithQueueName(InventoryQueueName)
                            .WithQueueType(QueueType.Classic)
                            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(1))
                    )
                    .Consumes<ProcessOrderCommand>(m =>
                        m.WithHandler<ProcessOrderHandler>($"{ScenarioSlug}.process")
                    )
                    .UseInbox<ConsumerDbContext>()
        );
    }

    public string Slug => ScenarioSlug;

    public string Title => "Inventory inbox retry then success";

    public string Description =>
        "ProcessOrderCommand fails twice then succeeds; order reaches Fulfilled.";

    public string Topic => "Inbox";

    public async Task<ScenarioVerdict> ExecuteAsync(
        ScenarioExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var runId = context.ScenarioRunId;
        var orderId = await this.StageOrderWithCommandAsync(
            context.PublisherDb,
            context.TimeProvider,
            runId,
            (id, rid) => new ProcessOrderCommand(id, rid),
            cancellationToken
        );
        context.StepsCompleted.Add("inventory_succeed_after_two_failures");
        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            orderId,
            OrderStatus.Fulfilled,
            ScenarioTiming.OrderEventuallyLong,
            context.TimeProvider,
            cancellationToken
        );
    }

    [RatatoskrMessage("inbox-retry-then-success.process-order-command")]
    public sealed record ProcessOrderCommand(string OrderId, string ScenarioRunId)
        : IPlaygroundCorrelatedOrderMessage;

    [RatatoskrMessage("inbox-retry-then-success.order-fulfilled")]
    public sealed record OrderFulfilled(string OrderId, string ScenarioRunId)
        : IPlaygroundCorrelatedOrderMessage;

    public sealed class ProcessOrderHandler(ConsumerDbContext context)
        : IMessageHandler<ProcessOrderCommand>
    {
        private static readonly ConcurrentDictionary<string, int> DeliveryAttempts = new();

        public async Task HandleAsync(
            ProcessOrderCommand message,
            MessageProperties properties,
            CancellationToken cancellationToken
        )
        {
            _ = properties;
            var key = $"{message.ScenarioRunId}:{message.OrderId}";
            var n = DeliveryAttempts.AddOrUpdate(key, 1, (_, old) => old + 1);
            if (n <= 2)
            {
                throw new InvalidOperationException(
                    "Simulated consumer failure (succeed-after-2)."
                );
            }

            try
            {
                var orderGuid = Guid.Parse(message.OrderId);
                context.OutboxMessages.Add(
                    new OrderFulfilled(message.OrderId, message.ScenarioRunId),
                    new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) }
                );
                await context.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                DeliveryAttempts.TryRemove(key, out _);
            }
        }
    }

    public sealed class OrderFulfilledHandler(PublisherDbContext context, TimeProvider timeProvider)
        : IMessageHandler<OrderFulfilled>
    {
        public Task HandleAsync(
            OrderFulfilled message,
            MessageProperties properties,
            CancellationToken cancellationToken
        ) =>
            IScenarioExtensions.UpdateOrderStatusAsync(
                null!,
                context,
                timeProvider,
                message.OrderId,
                OrderStatus.Fulfilled,
                cancellationToken
            );
    }
}
