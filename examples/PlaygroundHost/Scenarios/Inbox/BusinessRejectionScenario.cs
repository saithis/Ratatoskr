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

public sealed class BusinessRejectionScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "business-rejection";
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
            c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<OrderFailed>()
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
                    .Consumes<OrderFailed>(m =>
                        m.WithHandler<OrderFailedHandler>($"{ScenarioSlug}.failed")
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

    public string Title => "Business rejection (OrderFailed)";

    public string Description =>
        "Inventory reject mode stages OrderFailed; publisher marks the order Failed.";

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
        context.StepsCompleted.Add("inventory_reject_mode");
        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            orderId,
            OrderStatus.Failed,
            ScenarioTiming.OrderEventuallyLong,
            context.TimeProvider,
            cancellationToken
        );
    }

    [RatatoskrMessage("business-rejection.process-order-command")]
    public sealed record ProcessOrderCommand(string OrderId, string ScenarioRunId)
        : IPlaygroundCorrelatedOrderMessage;

    [RatatoskrMessage("business-rejection.order-failed")]
    public sealed record OrderFailed(string OrderId, string ScenarioRunId, string Reason)
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
                new OrderFailed(
                    message.OrderId,
                    message.ScenarioRunId,
                    "Simulated business rejection."
                ),
                new MessageProperties { Id = PlaygroundMessageIds.OrderFailed(orderGuid) }
            );
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public sealed class OrderFailedHandler(PublisherDbContext context, TimeProvider timeProvider)
        : IMessageHandler<OrderFailed>
    {
        public Task HandleAsync(
            OrderFailed message,
            MessageProperties _,
            CancellationToken cancellationToken
        ) =>
            IScenarioExtensions.UpdateOrderStatusAsync(
                null!,
                context,
                timeProvider,
                message.OrderId,
                OrderStatus.Failed,
                cancellationToken
            );
    }
}
