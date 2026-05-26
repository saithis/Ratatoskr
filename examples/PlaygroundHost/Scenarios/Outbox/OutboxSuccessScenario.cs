using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.Outbox;

public sealed class OutboxSuccessScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "outbox-success";
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
                            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5))
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
                            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5))
                    )
                    .Consumes<ProcessOrderCommand>(m =>
                        m.WithHandler<ProcessOrderHandler>($"{ScenarioSlug}.process")
                    )
                    .UseInbox<ConsumerDbContext>()
        );
    }

    public string Slug => ScenarioSlug;

    public string Title => "Outbox happy path";

    public string Description =>
        "Publisher stages ProcessOrderCommand in outbox; consumer inbox processes it and stages OrderFulfilled; publisher inbox marks the order Fulfilled.";

    public string Topic => "Outbox";

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
        context.StepsCompleted.Add("order_persisted_outbox_staged");
        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            orderId,
            OrderStatus.Fulfilled,
            ScenarioTiming.OrderEventuallyMedium,
            context.TimeProvider,
            cancellationToken
        );
    }

    [RatatoskrMessage("outbox-success.process-order-command")]
    public sealed record ProcessOrderCommand(string OrderId, string ScenarioRunId)
        : IPlaygroundCorrelatedOrderMessage;

    [RatatoskrMessage("outbox-success.order-fulfilled")]
    public sealed record OrderFulfilled(string OrderId, string ScenarioRunId)
        : IPlaygroundCorrelatedOrderMessage;

    public sealed class ProcessOrderHandler(ConsumerDbContext context)
        : IMessageHandler<ProcessOrderCommand>
    {
        public async Task HandleAsync(
            ProcessOrderCommand message,
            MessageProperties properties,
            CancellationToken cancellationToken
        )
        {
            var orderGuid = Guid.Parse(message.OrderId);
            context.OutboxMessages.Add(
                new OrderFulfilled(message.OrderId, message.ScenarioRunId),
                new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) }
            );
            await context.SaveChangesAsync(cancellationToken);
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
